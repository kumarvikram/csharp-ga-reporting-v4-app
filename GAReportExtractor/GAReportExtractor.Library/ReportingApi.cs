﻿using GAReportExtractor.Library.Configuration;
using Google.Apis.AnalyticsReporting.v4;
using Google.Apis.AnalyticsReporting.v4.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using GAReport = Google.Apis.AnalyticsReporting.v4.Data.Report;
using Report = GAReportExtractor.Library.Configuration.Report;

namespace GAReportExtractor.Library
{
    public class ReportingApi
    {
        private static readonly log4net.ILog Logger = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly AnalyticsReportingService analyticsReportingServiceInstance;
        /// <summary>
        /// Intializes and returns Analytics Reporting Service Instance using the parameters stored in key file
        /// </summary>
        /// <returns>AnalyticsReportingService</returns>   
        private AnalyticsReportingService GetAnalyticsReportingServiceInstance()
        {
            string[] scopes = { AnalyticsReportingService.Scope.AnalyticsReadonly }; //Read-only access to Google Analytics
            GoogleCredential credential;
            using (var stream = new FileStream(ConfigurationManager.AppSettings["KeyFileName"], FileMode.Open, FileAccess.Read))
            {
                credential = GoogleCredential.FromStream(stream).CreateScoped(scopes);
            }
            // Create the  Analytics service.
            var baseClientInitializer = new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = "GA Extractor",
            };
            return new AnalyticsReportingService(baseClientInitializer);
        }
        public ReportingApi()
        {
            this.analyticsReportingServiceInstance = GetAnalyticsReportingServiceInstance();
        }

        /// <summary>
        /// Create date range based on entries in configuration
        /// </summary>
        /// <param name="config"></param>
        /// <returns>List<DateRange></returns>
        private List<DateRange> GetDateRangeFromConfiguration(ReportConfiguration config)
        {
            var strStartDateFromConfig = config.DateConfiguration.StartDate;
            var strEndDateFromConfig = config.DateConfiguration.EndDate;
            var strNumberOfDaysFromConfig = config.DateConfiguration.NumberOfDays;

            DateTime.TryParse(strStartDateFromConfig, out DateTime reportStartDate);

            DateTime.TryParse(strEndDateFromConfig, out DateTime reportEndDate);

            int.TryParse(strNumberOfDaysFromConfig, out int numberOfDays);

            //Set start and end date for report using number of days
            var startDate = DateTime.Now.AddDays(-numberOfDays);
            var endDate = numberOfDays == 0 ? DateTime.Now : DateTime.Now.AddDays(-1);

            //Use start and end date from config if specified else keep the existing values
            if (reportStartDate != DateTime.MinValue && reportEndDate != DateTime.MinValue &&
                reportStartDate <= reportEndDate)
            {
                startDate = reportStartDate;
                endDate = reportEndDate;
            }

            return new List<DateRange>
                {
                    new DateRange
                    {
                        StartDate = startDate.ToString("yyyy-MM-dd"),
                        EndDate = endDate.ToString("yyyy-MM-dd")
                    }
                };
        }

        /// <summary>
        /// Get all reports configured in App.config
        /// </summary>
        /// <returns></returns>
        public GetReportsResponse GetReport(string viewId)
        {
            var combinedReportResponse = new GetReportsResponse { Reports = new List<GAReport>() };
            try
            {
                Logger.Info("Processing View Id: " + viewId);
                var config = ReportConfiguration.GetConfig();                               
                foreach (var item in config.Reports)
                {
                    if (item is Report report)
                    {
                        var stopwatch = new Stopwatch();
                        Logger.Info("Started fetching report: " + report.Name);
                        // Create the Metrics and dimensions object based on configuration.
                        var metrics = report.Metrics.Split(',').Select(m => new Metric { Expression = m }).ToList();
                        var dimensions = report.Dimensions.Split(',').Select(d => new Dimension { Name = d }).ToList();
                        var reportRequest = new ReportRequest
                        {
                            DateRanges = GetDateRangeFromConfiguration(config),
                            Metrics = metrics,
                            Dimensions = dimensions,
                            ViewId = viewId.Trim(),
                            SamplingLevel = "LARGE", //https://developers.google.com/analytics/devguides/reporting/core/v4/basics#sampling
                            PageSize = report.RecordCount>0? report.RecordCount : 10000 //The Analytics Core Reporting API returns a maximum of 10,000 rows per request, no matter how many you ask for. https://developers.google.com/analytics/devguides/reporting/core/v4/rest/v4/reports/batchGet
                        };
                        //Sorting as per report configuration 
                        if (!string.IsNullOrEmpty(report.OrderBy))
                        {
                            reportRequest.OrderBys = report.OrderBy.Split(',').Select(o => new OrderBy { FieldName = o.Split('-')[0], SortOrder = o.Split('-')[1] }).ToList();                                
                        }
                        stopwatch.Start();
                        var reportsResponse = analyticsReportingServiceInstance.Reports.BatchGet(new GetReportsRequest
                        {
                            ReportRequests = new List<ReportRequest> { reportRequest }
                        }).Execute();

                        if (reportsResponse != null)
                        {
                            ((List<GAReport>)combinedReportResponse.Reports).AddRange(reportsResponse.Reports);
                            while (reportsResponse.Reports[0].NextPageToken != null)
                            {
                                reportRequest.PageToken = reportsResponse.Reports[0].NextPageToken;
                                reportsResponse = analyticsReportingServiceInstance.Reports.BatchGet(new GetReportsRequest
                                {
                                    ReportRequests = new List<ReportRequest> { reportRequest }
                                }).Execute();
                                ((List<GAReport>)combinedReportResponse.Reports).AddRange(reportsResponse.Reports);
                            }
                            stopwatch.Stop();                            
                        }
                        Logger.Info("Finished fetching report: " + report.Name);
                        Logger.Info(string.Format("Time elapsed: {0:hh\\:mm\\:ss}", stopwatch.Elapsed));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error in fetching reports: " + ex);
            }
            return combinedReportResponse;
        }

    }
}
