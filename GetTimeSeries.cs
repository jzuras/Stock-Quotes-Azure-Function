using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using Learning.StockQuotes.TwelveDataErrorNS;
using Learning.StockQuotes.TwelveDataTimeSeriesNS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;

namespace Learning.StockQuotes
{
    public class GetTimeSeries
    {
        // this is the return data
        private record TimeSeriesStockData(string X, double Y);
        private record ErrorData(long code, string message, string status);
        // note - only one of the above two records will be populated in returned data
        private record ReturnedData(TimeSeriesStockData[] stockData, ErrorData errorData, bool error);


        private readonly ILogger<GetTimeSeries> mLogger;

        private string mTwelveDataURL = "https://api.twelvedata.com/time_series?interval=5min&order=ASC&apikey=";

        public GetTimeSeries(ILogger<GetTimeSeries> log)
        {
            this.mLogger = log;
        }

        [FunctionName("GetTimeSeries")]
        [OpenApiOperation(operationId: "Run", tags: new[] { "symbol" })]
        [OpenApiParameter(name: "symbol", In = ParameterLocation.Query, Required = true, Type = typeof(string), Description = "The stock symbol for desired quote")]
        [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "text/plain", bodyType: typeof(string), Description = "The OK response")]
        /// <summary>
        /// Azure Function which calls TwelveData api for getting time series data.
        /// Returns either an array of X,Y values for charting purposes:
        /// X is a string with the time of the price quote,  Y is a double with the price quote.
        /// or an error with the following information:
        /// long Code, string Message, string Status
        /// </summary>
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req)
        {
            // leaving this "printf" here so I can find it in Azure logging data
            this.mLogger.LogInformation("C# HTTP trigger function GetTimeSeries processed a request.");

            string symbolToUse = req.Query["symbol"];
            if (symbolToUse == null)
            {
                return (ActionResult)new OkObjectResult(this.ErrorToReturn("Please provide a value for 'symbol' parameter.",
                    "Request did not contain symbol to use for quote."));
            }

            try
            {
                DateTime dateToTry = DateTime.Today;
                // note - return type is either TwelveDataTimeSeries or TwelveDataError
                Object timeSeriesData = await this.GetTimeSeriesForDate(symbolToUse, dateToTry);

                // timeSeries api call may not return data when market did not open for the day (weekend/holiday)
                // or has not yet opened (pre-market).
                // in these cases, we attempt to get yesterday's data.
                // if still an issue, keep going back one day, but try a max of 4 times
                // (if the market was closed for more than 4 days, throw error).
                int tryCount = 1;
                bool success = false;
                while (tryCount < 5 && !success)
                {
                    // returned data may not be a TimeSeries
                    if (timeSeriesData.GetType() == typeof(TwelveDataTimeSeries))
                    {
                        // we have what we need, return it
                        success = true;
                    }
                    else if (timeSeriesData.GetType() == typeof(TwelveDataError))
                    {
                        TwelveDataError tmp = timeSeriesData as TwelveDataError;

                        // check for this expected error message meaning market closed:
                        // "No data is available on the specified dates. Try setting different start/end dates."
                        // this message means we should try going back a day for data
                        // keep trying by going back a day
                        if (tmp.Message == "No data is available on the specified dates. Try setting different start/end dates.")
                        {
                            tryCount++;
                            dateToTry = dateToTry.AddDays(-1);
                            timeSeriesData = await this.GetTimeSeriesForDate(symbolToUse, dateToTry);
                        }
                        else
                        {
                            // unexpected error from TwelveData, which could be as simple as unknown stock symbol,
                            // so bail out and pass the error message back to caller.
                            return (ActionResult) new OkObjectResult(this.ErrorToReturn(tmp.Message, ""));
                        }
                    }
                    else
                    {
                        // we really shouldn't get here - bail out so we don't have an infinite loop just in case
                        return (ActionResult) new OkObjectResult(this.ErrorToReturn("Unable to Process Request.",
                            "GetTimeSeriesForDate method returned unexpected type while trying to get or parse JSON data."));
                    }
                }
                if (success)
                {
                    return (ActionResult) new OkObjectResult(this.MergeQuote(timeSeriesData as TwelveDataTimeSeries));
                }

                // maximum attempts reached - return error message
                return (ActionResult) new OkObjectResult(this.ErrorToReturn("Unable to find a day that market was open.", ""));
            }
            catch (Exception ex)
            {
                // return unknown error to caller
                return (ActionResult) new OkObjectResult(this.ErrorToReturn("Unable to Process Request.",
                    "Exception caught trying to get or parse JSON data. Exception is:\n" + ex.ToString()));
            }
        }


        /// <summary>
        /// This is the method that actually does the api call to TwelveData for TimeSeries info.
        /// It is separated out so that it can be called again with a different date when needed.
        /// </summary>
        /// <param name="symbolToUse">Stock Symbol for which to get data - provided by caller.</param>
        /// <param name="dateToTry">Date to try. Calling code should start with Today,
        /// then move back 1 day at a time and try again.</param>
        /// <returns>Returns TwelveDataTimeSeries if data is available, 
        /// or TwelveDataError if market closed or an unexpected error occurs.</returns>
        private async Task<Object> GetTimeSeriesForDate(string symbolToUse, DateTime dateToTry)
        {
            TwelveDataTimeSeries quote = null;

            string dateInISOFormat = dateToTry.ToString("u"); // format: 1970-01-01T00:00:00.000Z
            string dateOnly = dateInISOFormat.Substring(0, 10);

            string apiKey = Environment.GetEnvironmentVariable("TWELVEDATA_API_KEY");
            string apiString = this.mTwelveDataURL + HttpUtility.UrlEncode(apiKey) + "&symbol=" + HttpUtility.UrlEncode(symbolToUse) +
                "&date=" + dateOnly;

            try
            {
                HttpClient client = new HttpClient();
                HttpResponseMessage response = await client.GetAsync(apiString);
                if (response.IsSuccessStatusCode)
                {
                    quote = await response.Content.ReadAsAsync<TwelveDataTimeSeries>();
                    if (quote.Meta == null || quote.Status == "error")
                    {
                        // try to parse JSON again as an error response from TwelveData
                        HttpResponseMessage response2 = await client.GetAsync(apiString);
                        if (response2.IsSuccessStatusCode)
                        {
                            TwelveDataError error = await response2.Content.ReadAsAsync<TwelveDataError>();
                            if (error.Status != null)
                            {
                                // this is a valid error message from TwelveData, so return it
                                // (it may be an expected error due to closed market - 
                                // caller should handle it by changing date and trying again)
                                return error;
                            }
                            else
                            {
                                // return unknown error to caller
                                string tmp = "Unable to Process Request. Status: " + error.Status + " Message: " + error.Message;
                                return this.ErrorToReturn(tmp,
                                    "JSON returned from TwelveData was not Quote as expected, nor was it Error.\n" + tmp);
                            }
                        }
                        else
                        {
                            // response2 code indicated HTTP failure
                            return this.ErrorToReturn("Unable to Process Request. " +
                                "HTTP Response2 Status Code: " + response2.StatusCode +
                                "HTTP Response2 Message: " + response2.ReasonPhrase, "");
                        }
                    }
                }
                else
                {
                    // response code indicated HTTP failure
                    return this.ErrorToReturn("Unable to Process Request. " +
                        "HTTP Response Status Code: " + response.StatusCode +
                        "HTTP Response Message: " + response.ReasonPhrase, "");
                }

                return quote;
            }
            catch (System.Exception ex)
            {
                // return unknown error to caller
                return this.ErrorToReturn("Unable to Process Request.",
                    "Exception caught trying to get or parse JSON data. Exception is:\n" + ex.ToString());
            }
        }


        /// <summary>
        /// Populates ReturnedData with the data and an empty error object.
        /// </summary>
        /// <param name="timeSeriesData">Quotes over time.</param>
        /// <returns>ReturnedData object ready to return to caller.</returns>
        private ReturnedData MergeQuote(TwelveDataTimeSeries timeSeriesData)
        {
            ReturnedData returnValue;
            ErrorData error = new ErrorData(0, "", "");
            TimeSeriesStockData[] data = new TimeSeriesStockData[timeSeriesData.Values.Count];

            for (int i = 0; i < data.Length; i++)
            {
                data[i] = new TimeSeriesStockData(timeSeriesData.Values[i].Datetime.ToString(),
                    double.Parse(timeSeriesData.Values[i].Close));
            }

            returnValue = new ReturnedData(data, error, false);
            return returnValue;
        }


        /// <summary>
        /// Populates ReturnedData with an error object and empty data, and logs internal error info. 
        /// </summary>
        /// <param name="aError">Error info from TwelveData.</param>
        /// <returns>ReturnedData object ready to return to caller.</returns>
        private ReturnedData ErrorToReturn(TwelveDataError aError)
        {
            TimeSeriesStockData[] data = new TimeSeriesStockData[0];
            ErrorData error = new ErrorData(aError.Code, aError.Message, aError.Status);
            ReturnedData returnValue = new ReturnedData(data, error, true);

            this.mLogger.LogInformation("C# HTTP trigger function GetTimeSeries encountered an error from TwelveData. Error is:\n"
                + aError.Message + "(Code: " + aError.Code + ", status: " + aError.Status);

            return returnValue;
        }


        /// <summary>
        /// Populates ReturnedData with an error object and empty data, and logs internal error info. 
        /// </summary>
        /// <param name="message">Error message to return to caller.</param>
        /// <param name="messageToLog">Internal-use-only error message to log.</param>
        /// <returns>ReturnedData object ready to return to caller.</returns>
        private ReturnedData ErrorToReturn(string message, string messageToLog)
        {
            TimeSeriesStockData[] data = new TimeSeriesStockData[0];
            ErrorData error = new ErrorData(0, message, "");
            ReturnedData returnValue = new ReturnedData(data, error, true);

            if (string.IsNullOrEmpty(messageToLog))
            {
                messageToLog = message;
            }

            this.mLogger.LogInformation("C# HTTP trigger function GetTimeSeries encountered an unexpected error. Error is:\n"
                + messageToLog ?? message);

            return returnValue;
        }
    }
}
