﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Aquarius.TimeSeries.Client;
using Aquarius.TimeSeries.Client.ServiceModels.Publish;
using Humanizer;
using log4net;
using ServiceStack;
using TimeSeriesDescription = Aquarius.TimeSeries.Client.ServiceModels.Publish.TimeSeriesDescription;

namespace SosExporter
{
    public class Exporter
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public Context Context { get; set; }

        private IAquariusClient Aquarius { get; set; }
        private ISosClient Sos { get; set; }
        private SyncStatus SyncStatus { get; set; }
        private TimeSeriesPointFilter TimeSeriesPointFilter { get; set; }
        private long ExportedPointCount { get; set; }
        private int ExportedTimeSeriesCount { get; set; }

        public void Run()
        {
            Log.Info($"{GetProgramVersion()} connecting to {Context.Config.AquariusServer} ...");

            using (Aquarius = CreateConnectedAquariusClient())
            {
                Log.Info($"Connected to {Context.Config.AquariusServer} (v{Aquarius.ServerVersion}) as {Context.Config.AquariusUsername}");

                if (Aquarius.ServerVersion.IsLessThan(MinimumVersion))
                    throw new ExpectedException($"This utility requires AQTS v{MinimumVersion} or greater.");

                var stopwatch = Stopwatch.StartNew();

                RunOnce();

                Log.Info($"Successfully exported {ExportedPointCount} points from {ExportedTimeSeriesCount} time-series in {stopwatch.Elapsed.Humanize()}");
            }
        }

        private static readonly AquariusServerVersion MinimumVersion = AquariusServerVersion.Create("17.2");

        private static string GetProgramVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);

            // ReSharper disable once PossibleNullReferenceException
            return $"{MethodBase.GetCurrentMethod().DeclaringType.Namespace} v{fileVersionInfo.FileVersion}";
        }

        private IAquariusClient CreateConnectedAquariusClient()
        {
            var client = AquariusClient.CreateConnectedClient(
                Context.Config.AquariusServer,
                Context.Config.AquariusUsername,
                Context.Config.AquariusPassword);

            foreach (var serviceClient in new[]{client.Publish, client.Provisioning, client.Acquisition})
            {
                var jsonClient = serviceClient as JsonServiceClient;

                if (jsonClient == null)
                    continue;

                jsonClient.Timeout = Context.Timeout;
                jsonClient.ReadWriteTimeout = Context.Timeout;
            }

            return client;
        }

        private void LogDryRun(string message)
        {
            Log.Warn($"Dry-run: {message}");
        }

        private void RunOnce()
        {
            SyncStatus = new SyncStatus(Aquarius) {Context = Context};
            TimeSeriesPointFilter = new TimeSeriesPointFilter {Context = Context};

            ValidateFilters();

            var request = CreateFilterRequest();

            if (Context.ForceResync)
            {
                Log.Warn("Forcing a full time-series resync.");
                request.ChangesSinceToken = null;
            }
            else if (Context.ChangesSince.HasValue)
            {
                Log.Warn($"Overriding current ChangesSinceToken='{request.ChangesSinceToken:O}' with '{Context.ChangesSince:O}'");
                request.ChangesSinceToken = Context.ChangesSince.Value.UtcDateTime;
            }

            Log.Info($"Checking {GetFilterSummary(request)} ...");

            var stopwatch = Stopwatch.StartNew();

            var response = Aquarius.Publish.Get(request);

            if (response.TokenExpired ?? false)
            {
                if (Context.NeverResync)
                {
                    Log.Warn("Skipping a recommended resync.");
                }
                else
                {
                    Log.Warn($"The ChangesSinceToken of {request.ChangesSinceToken:O} has expired. Forcing a full resync. You may need to run the exporter more frequently.");
                    request.ChangesSinceToken = null;

                    response = Aquarius.Publish.Get(request);
                }
            }

            var bootstrapToken = response.ResponseTime
                .Subtract(stopwatch.Elapsed)
                .Subtract(TimeSpan.FromMinutes(1))
                .UtcDateTime;

            var nextChangesSinceToken = response.NextToken ?? bootstrapToken;

            Log.Info($"Fetching descriptions of {response.TimeSeriesUniqueIds.Count} changed time-series ...");

            var timeSeriesDescriptions = FetchChangedTimeSeriesDescriptions(
                response.TimeSeriesUniqueIds
                    .Select(ts => ts.UniqueId)
                    .ToList());

            Log.Info($"Connecting to {Context.Config.SosServer} ...");

            using (Sos = SosClient.CreateConnectedClient(Context))
            {
                Log.Info($"Connected to {Context.Config.SosServer} as {Context.Config.SosUsername}");

                ExportToSos(request, response, timeSeriesDescriptions);
            }

            SyncStatus.SaveConfiguration(nextChangesSinceToken);
        }

        private void ValidateFilters()
        {
            ValidateApprovalFilters();
            ValidateGradeFilters();
            ValidateQualifierFilters();
        }

        private void ValidateApprovalFilters()
        {
            if (!Context.Config.Approvals.Any()) return;

            Log.Info("Fetching approval configuration ...");
            var approvals = Aquarius.Publish.Get(new ApprovalListServiceRequest()).Approvals;

            foreach (var approvalFilter in Context.Config.Approvals)
            {
                var approvalMetadata = approvals.SingleOrDefault(a =>
                    a.DisplayName.Equals(approvalFilter.Text, StringComparison.InvariantCultureIgnoreCase)
                    || a.Identifier.Equals(approvalFilter.Text, StringComparison.InvariantCultureIgnoreCase));

                if (approvalMetadata == null)
                    throw new ExpectedException($"Unknown approval '{approvalFilter.Text}'");

                approvalFilter.Text = approvalMetadata.DisplayName;
                approvalFilter.ApprovalLevel = int.Parse(approvalMetadata.Identifier);
            }
        }

        private void ValidateGradeFilters()
        {
            if (!Context.Config.Grades.Any()) return;

            Log.Info("Fetching grade configuration ...");
            var grades = Aquarius.Publish.Get(new GradeListServiceRequest()).Grades;

            foreach (var gradeFilter in Context.Config.Grades)
            {
                var gradeMetadata = grades.SingleOrDefault(g =>
                    g.DisplayName.Equals(gradeFilter.Text, StringComparison.InvariantCultureIgnoreCase)
                    || g.Identifier.Equals(gradeFilter.Text, StringComparison.InvariantCultureIgnoreCase));

                if (gradeMetadata == null)
                    throw new ExpectedException($"Unknown grade '{gradeFilter.Text}'");

                gradeFilter.Text = gradeMetadata.DisplayName;
                gradeFilter.GradeCode = int.Parse(gradeMetadata.Identifier);
            }
        }

        private void ValidateQualifierFilters()
        {
            if (!Context.Config.Qualifiers.Any()) return;

            Log.Info("Fetching qualifier configuration ...");
            var qualifiers = Aquarius.Publish.Get(new QualifierListServiceRequest()).Qualifiers;

            foreach (var qualifierFilter in Context.Config.Qualifiers)
            {
                var qualifierMetadata = qualifiers.SingleOrDefault(q =>
                    q.Identifier.Equals(qualifierFilter.Text, StringComparison.InvariantCultureIgnoreCase)
                    || q.Code.Equals(qualifierFilter.Text, StringComparison.InvariantCultureIgnoreCase));

                if (qualifierMetadata == null)
                    throw new ExpectedException($"Unknown qualifier '{qualifierFilter.Text}'");

                qualifierFilter.Text = qualifierMetadata.Identifier;
            }
        }

        private TimeSeriesUniqueIdListServiceRequest CreateFilterRequest()
        {
            var locationIdentifier = Context.Config.LocationIdentifier;

            if (!string.IsNullOrEmpty(locationIdentifier))
            {
                var locationDescription = Aquarius.Publish
                    .Get(new LocationDescriptionListServiceRequest { LocationIdentifier = locationIdentifier })
                    .LocationDescriptions
                    .SingleOrDefault();

                if (locationDescription == null)
                    throw new ExpectedException($"Location '{locationIdentifier}' does not exist.");

                locationIdentifier = locationDescription.Identifier;
            }

            return new TimeSeriesUniqueIdListServiceRequest
            {
                ChangesSinceToken = SyncStatus.GetLastChangesSinceToken(),
                LocationIdentifier = locationIdentifier,
                ChangeEventType = Context.Config.ChangeEventType?.ToString(),
                Publish = Context.Config.Publish,
                Parameter = Context.Config.Parameter,
                ComputationIdentifier = Context.Config.ComputationIdentifier,
                ComputationPeriodIdentifier = Context.Config.ComputationPeriodIdentifier,
                ExtendedFilters = Context.Config.ExtendedFilters.Any() ? Context.Config.ExtendedFilters : null,
            };
        }

        private string GetFilterSummary(TimeSeriesUniqueIdListServiceRequest request)
        {
            var sb = new StringBuilder();

            sb.Append(string.IsNullOrEmpty(request.LocationIdentifier)
                ? "all locations"
                : $"location '{request.LocationIdentifier}'");

            var filters = new List<string>();

            if (request.Publish.HasValue)
            {
                filters.Add($"Publish={request.Publish}");
            }

            if (!string.IsNullOrEmpty(request.Parameter))
            {
                filters.Add($"Parameter={request.Parameter}");
            }

            if (!string.IsNullOrEmpty(request.ComputationIdentifier))
            {
                filters.Add($"ComputationIdentifier={request.ComputationIdentifier}");
            }

            if (!string.IsNullOrEmpty(request.ComputationPeriodIdentifier))
            {
                filters.Add($"ComputationPeriodIdentifier={request.ComputationPeriodIdentifier}");
            }

            if (!string.IsNullOrEmpty(request.ChangeEventType))
            {
                filters.Add($"ChangeEventType={request.ChangeEventType}");
            }

            if (request.ExtendedFilters != null && request.ExtendedFilters.Any())
            {
                filters.Add($"ExtendedFilters={string.Join(", ", request.ExtendedFilters.Select(f => $"{f.FilterName}={f.FilterValue}"))}");
            }

            if (filters.Any())
            {
                sb.Append($" with {string.Join(" and ", filters)}");
            }

            sb.Append(" for time-series");

            if (request.ChangesSinceToken.HasValue)
            {
                sb.Append($" change since {request.ChangesSinceToken:O}");
            }

            return sb.ToString();
        }


        private List<TimeSeriesDescription> FetchChangedTimeSeriesDescriptions(List<Guid> timeSeriesUniqueIdsToFetch)
        {
            var timeSeriesDescriptions = new List<TimeSeriesDescription>();

            using (var batchClient = CreatePublishClientWithPostMethodOverride())
            {
                while (timeSeriesUniqueIdsToFetch.Any())
                {
                    const int batchSize = 400;

                    var batchList = timeSeriesUniqueIdsToFetch.Take(batchSize).ToList();
                    timeSeriesUniqueIdsToFetch = timeSeriesUniqueIdsToFetch.Skip(batchSize).ToList();

                    var request = new TimeSeriesDescriptionListByUniqueIdServiceRequest();

                    // We need to resolve the URL without any unique IDs on the GET command line
                    var requestUrl = RemoveQueryFromUrl(request.ToGetUrl());

                    request.TimeSeriesUniqueIds = batchList;

                    var batchResponse =
                        batchClient.Send<TimeSeriesDescriptionListByUniqueIdServiceResponse>(HttpMethods.Post,
                            requestUrl, request);

                    timeSeriesDescriptions.AddRange(batchResponse.TimeSeriesDescriptions);
                }
            }

            return timeSeriesDescriptions
                .OrderBy(ts => ts.LocationIdentifier)
                .ThenBy(ts => ts.Identifier)
                .ToList();
        }

        private JsonServiceClient CreatePublishClientWithPostMethodOverride()
        {
            return Aquarius.CloneAuthenticatedClientWithOverrideMethod(Aquarius.Publish, HttpMethods.Get) as JsonServiceClient;
        }

        private static string RemoveQueryFromUrl(string url)
        {
            var queryIndex = url.IndexOf("?", StringComparison.InvariantCulture);

            if (queryIndex < 0)
                return url;

            return url.Substring(0, queryIndex);
        }

        private List<TimeSeriesDescription> FilterTimeSeriesDescriptions(List<TimeSeriesDescription> timeSeriesDescriptions)
        {
            if (!Context.Config.TimeSeries.Any())
                return timeSeriesDescriptions;

            var timeSeriesFilter = new Filter<TimeSeriesFilter>(Context.Config.TimeSeries);

            var results = new List<TimeSeriesDescription>();

            foreach (var timeSeriesDescription in timeSeriesDescriptions)
            {
                if (timeSeriesFilter.IsFiltered(f => f.Regex.IsMatch(timeSeriesDescription.Identifier)))
                    continue;

                results.Add(timeSeriesDescription);
            }

            return results;
        }

        private void ExportToSos(
            TimeSeriesUniqueIdListServiceRequest request,
            TimeSeriesUniqueIdListServiceResponse response,
            List<TimeSeriesDescription> timeSeriesDescriptions)
        {
            var filteredTimeSeriesDescriptions = FilterTimeSeriesDescriptions(timeSeriesDescriptions);

            Log.Info($"Exporting {filteredTimeSeriesDescriptions.Count} time-series ...");

            var clearExportedData = !request.ChangesSinceToken.HasValue;

            if (clearExportedData)
            {
                ClearExportedData();
            }

            foreach (var timeSeriesDescription in filteredTimeSeriesDescriptions)
            {
                ExportTimeSeries(
                    clearExportedData,
                    response.TimeSeriesUniqueIds.Single(t => t.UniqueId == timeSeriesDescription.UniqueId),
                    timeSeriesDescription);
            }
        }

        private void ClearExportedData()
        {
            if (Context.DryRun)
            {
                LogDryRun("Would have cleared the SOS database of all existing data.");
                return;
            }

            Sos.ClearDatasource();
            Sos.DeleteDeletedObservations();
        }

        private void ExportTimeSeries(
            bool clearExportedData,
            TimeSeriesUniqueIds detectedChange,
            TimeSeriesDescription timeSeriesDescription)
        {
            Log.Info($"Fetching changes from '{timeSeriesDescription.Identifier}' FirstPointChanged={detectedChange.FirstPointChanged:O} HasAttributeChanged={detectedChange.HasAttributeChange} ...");

            var locationInfo = GetLocationInfo(timeSeriesDescription.LocationIdentifier);

            var period = GetTimeSeriesPeriod(timeSeriesDescription);

            var dataRequest = new TimeSeriesDataCorrectedServiceRequest
            {
                TimeSeriesUniqueId = timeSeriesDescription.UniqueId,
                QueryFrom = detectedChange.FirstPointChanged,
                ApplyRounding = true,
            };

            var timeSeries = FetchRecentSignal(timeSeriesDescription, dataRequest, ref period);
            var daysToExtract = Context.Config.MaximumPointDays[period];

            var existingSensor = Sos.FindExistingSensor(timeSeries);

            var deleteExistingSensor = clearExportedData && existingSensor != null;

            if (existingSensor?.PhenomenonTime.Last() >= detectedChange.FirstPointChanged)
            {
                // A point has changed before the last known observation, so we'll need to throw out the entire sensor
                deleteExistingSensor = true;

                // We'll also need to fetch more data again
                dataRequest.QueryFrom = null;
                timeSeries = FetchRecentSignal(timeSeriesDescription, dataRequest, ref period);
                daysToExtract = Context.Config.MaximumPointDays[period];
            }

            if (daysToExtract > 0 && timeSeries.Points.Any())
            {
                var earliestDayToUpload = SubtractTimeSpan(
                    timeSeries.Points.Last().Timestamp.DateTimeOffset,
                    TimeSpan.FromDays(daysToExtract));

                var remainingPoints = timeSeries.Points
                    .Where(p => p.Timestamp.DateTimeOffset >= earliestDayToUpload)
                    .ToList();

                var trimmedPointCount = timeSeries.NumPoints - remainingPoints.Count;

                Log.Info($"Trimming '{timeSeriesDescription.Identifier}' {trimmedPointCount} points before {earliestDayToUpload:O} with {remainingPoints.Count} points remaining with Frequency={period}");

                timeSeries.Points = remainingPoints;
                timeSeries.NumPoints = timeSeries.Points.Count;
            }

            TimeSeriesPointFilter.FilterTimeSeriesPoints(timeSeries);

            var createSensor = existingSensor == null || deleteExistingSensor;
            var assignedOffering = existingSensor?.Identifier;

            var exportSummary = $"{timeSeries.NumPoints} points [{timeSeries.Points.FirstOrDefault()?.Timestamp.DateTimeOffset:O} to {timeSeries.Points.LastOrDefault()?.Timestamp.DateTimeOffset:O}] from '{timeSeriesDescription.Identifier}' with Frequency={period}";

            ExportedTimeSeriesCount += 1;
            ExportedPointCount += timeSeries.NumPoints ?? 0;

            if (Context.DryRun)
            {
                if (deleteExistingSensor)
                    LogDryRun($"Would delete existing sensor '{existingSensor.Identifier}'");

                if (createSensor)
                    LogDryRun($"Would create new sensor for '{timeSeriesDescription.Identifier}'");

                LogDryRun($"Would export {exportSummary}.");
                return;
            }

            Log.Info($"Exporting {exportSummary} ...");

            if (deleteExistingSensor)
            {
                Sos.DeleteSensor(timeSeries);
                Sos.DeleteDeletedObservations();
            }

            if (createSensor)
            {
                var sensor = Sos.InsertSensor(timeSeries);

                assignedOffering = sensor.AssignedOffering;
            }

            Sos.InsertObservation(assignedOffering, locationInfo.LocationData, locationInfo.LocationDescription, timeSeries, timeSeriesDescription);
        }

        private (LocationDescription LocationDescription, LocationDataServiceResponse LocationData) GetLocationInfo(string locationIdentifier)
        {
            if (LocationInfoCache.TryGetValue(locationIdentifier, out var locationInfo))
                return locationInfo;

            var locationDescription = Aquarius.Publish.Get(new LocationDescriptionListServiceRequest
            {
                LocationIdentifier = locationIdentifier
            }).LocationDescriptions.Single();

            var locationData = Aquarius.Publish.Get(new LocationDataServiceRequest
            {
                LocationIdentifier = locationIdentifier
            });

            locationInfo = (locationDescription, locationData);

            LocationInfoCache.Add(locationIdentifier, locationInfo);

            return locationInfo;
        }

        private
            Dictionary<string, (LocationDescription LocationDescription, LocationDataServiceResponse LocationData)>
            LocationInfoCache { get; } =
                new Dictionary<string, (LocationDescription LocationDescription, LocationDataServiceResponse LocationData)>();

        private static DateTimeOffset SubtractTimeSpan(DateTimeOffset dateTimeOffset, TimeSpan timeSpan)
        {
            return dateTimeOffset.Subtract(DateTimeOffset.MinValue) <= timeSpan
                ? DateTimeOffset.MinValue
                : dateTimeOffset.Subtract(timeSpan);
        }

        private ComputationPeriod GetTimeSeriesPeriod(TimeSeriesDescription timeSeriesDescription)
        {
            if (Enum.TryParse<ComputationPeriod>(timeSeriesDescription.ComputationPeriodIdentifier, true, out var period))
            {
                if (period == ComputationPeriod.WaterYear)
                    period = ComputationPeriod.Annual; // WaterYear and Annual are the same frequency

                if (Context.Config.MaximumPointDays.ContainsKey(period))
                    return period;
            }

            // Otherwise fall back to the "I don't know" setting
            return ComputationPeriod.Unknown;
        }

        private TimeSeriesDataServiceResponse FetchRecentSignal(
            TimeSeriesDescription timeSeriesDescription,
            TimeSeriesDataCorrectedServiceRequest dataRequest,
            ref ComputationPeriod period)
        {
            var maximumDaysToExport = Context.Config.MaximumPointDays[period];
            TimeSpan retrievalDuration;

            if (dataRequest.QueryFrom != null)
            {
                // We've been told exactly how far back in time to pull the AQTS signal.
                // But for derived time-series, this starting point can often be "the beginning of time", which can be much too early
                retrievalDuration = DateTimeOffset.UtcNow.Subtract(dataRequest.QueryFrom.Value);

                if (maximumDaysToExport > 0 && retrievalDuration < TimeSpan.FromDays(maximumDaysToExport))
                {
                    // We know the frequency period of the time-series, and we know that we haven't exceeded the export configuration limit for that frequency.
                    // So we can confidently know that this is the only points-retrieval request needed from AQTS.
                    // This is the code path we want to hit for best incremental sync performance.
                    return Aquarius.Publish.Get(dataRequest);
                }
            }

            // If we find ourselves here, we will need to do a least one fetch of data to see if we have enough to satisfy the export request.
            var utcNow = DateTime.UtcNow.Date;
            var startOfToday = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0,
                timeSeriesDescription.UtcOffsetIsoDuration.ToTimeSpan());

            var queryStartPoint = dataRequest.QueryFrom ?? startOfToday.Subtract(TimeSpan.FromDays(90));
            dataRequest.QueryFrom = queryStartPoint;

            TimeSeriesDataServiceResponse timeSeries;

            if (period == ComputationPeriod.Unknown)
            {
                // We don't know the time-series period, so we'll need to load the most recent data until we can make a stronger inference about the frequency
                timeSeries = FetchRecentSignal(
                    timeSeriesDescription,
                    dataRequest,
                    ts => ts.NumPoints >= ComputationPeriodEstimator.MinimumPointCount,
                    "to determine signal frequency");

                period = ComputationPeriodEstimator.InferPeriodFromRecentPoints(timeSeries);
                maximumDaysToExport = Context.Config.MaximumPointDays[period];

                if (dataRequest.QueryFrom == null)
                {
                    // We've already asked for all the data
                    return timeSeries;
                }

                if (maximumDaysToExport > 0 && GetRetrievedDuration(timeSeries, dataRequest) >= TimeSpan.FromDays(maximumDaysToExport))
                {
                    // We have enough known points to satisfy the export request
                    return timeSeries;
                }
            }

            // We've seen enough of the most recent points to know the time-series period,
            // but we still don't know how much data to fetch.
            // Stop when we've retrieved at least this much data
            retrievalDuration = maximumDaysToExport > 0
                ? TimeSpan.FromDays(maximumDaysToExport)
                : TimeSpan.MaxValue;

            timeSeries = FetchRecentSignal(
                timeSeriesDescription,
                dataRequest,
                ts =>
                {
                    var duration = GetRetrievedDuration(ts, dataRequest);

                    return duration >= retrievalDuration;
                },
                $"with Frequency={period}");

            return timeSeries;
        }

        private static TimeSpan GetRetrievedDuration(
            TimeSeriesDataServiceResponse timeSeries,
            TimeSeriesDataCorrectedServiceRequest dataRequest)
        {
            return dataRequest.QueryFrom.HasValue
                ? timeSeries.NumPoints <= 0
                    ? TimeSpan.MinValue
                    : timeSeries.Points.Last().Timestamp.DateTimeOffset
                        .Subtract(dataRequest.QueryFrom.Value)
                : TimeSpan.MaxValue;
        }

        private TimeSeriesDataServiceResponse FetchRecentSignal(
            TimeSeriesDescription timeSeriesDescription,
            TimeSeriesDataCorrectedServiceRequest dataRequest,
            Func<TimeSeriesDataServiceResponse,bool> isDataFetchComplete,
            string progressMessage)
        {
            TimeSeriesDataServiceResponse timeSeries = null;

            foreach (var timeSpan in PeriodsToFetch)
            {
                if (timeSpan == TimeSpan.MaxValue)
                {
                    dataRequest.QueryFrom = null;
                }

                Log.Info($"Fetching more than changed points from '{timeSeriesDescription.Identifier}' with QueryFrom={dataRequest.QueryFrom:O} {progressMessage} ...");

                timeSeries = Aquarius.Publish.Get(dataRequest);

                if (timeSpan == TimeSpan.MaxValue || isDataFetchComplete(timeSeries) )
                    break;

                dataRequest.QueryFrom -= timeSpan;
            }

            if (timeSeries == null)
                throw new Exception($"Logic error: Can't fetch time-series data of '{timeSeriesDescription.Identifier}' {progressMessage}");

            return timeSeries;
        }

        private static readonly TimeSpan[] PeriodsToFetch =
            Enumerable.Repeat(TimeSpan.FromDays(90), 3)
                .Concat(Enumerable.Repeat(TimeSpan.FromDays(365), 4))
                .Concat(Enumerable.Repeat(TimeSpan.FromDays(5 * 365), 4))
                .Concat(new[] {TimeSpan.MaxValue})
                .ToArray();
    }
}
