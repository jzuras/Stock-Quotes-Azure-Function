using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using Learning.StockQuotes.TwelveDataErrorNS;
using Learning.StockQuotes.TwelveDataQuoteNS;
using Learning.StockQuotes.TwelveDataRealTimeQuoteNS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;

namespace Learning.StockQuotes
{
	public class GetStockQuote
	{
		// this is the return data
		private record StockData(string companySymbol, string companyName, double quote, DateTime timeOfQuote);
		private record ErrorData(long code, string message, string status);
		// note - only one of the above two records will be populated in returned data
		private record ReturnedData(StockData stockData, ErrorData errorData, bool error);

		private readonly ILogger<GetStockQuote> mLogger;

		private string mTwelveDataURL = "https://api.twelvedata.com/quote?apikey=";
		private string mTwelveDataRealTimeURL = "https://api.twelvedata.com/price?apikey=";

		public GetStockQuote(ILogger<GetStockQuote> log)
		{
			this.mLogger = log;
		}

		[FunctionName("GetStockQuote")]
		[OpenApiOperation(operationId: "Run", tags: new[] { "symbol" })]
		[OpenApiParameter(name: "symbol", In = ParameterLocation.Query, Required = true, Type = typeof(string), Description = "The stock symbol for desired quote")]
		[OpenApiParameter(name: "realtime", In = ParameterLocation.Query, Required = false, Type = typeof(bool), Description = "Optional parameter to get real-time data (value must be 1)")]
		[OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "text/plain", bodyType: typeof(string), Description = "The OK response")]
		/// <summary>
		/// Azure Function which calls TwelveData api for getting a single quote.
		/// Can also be called for a real-time quote (quote only, nothing else returned),
		/// in which case only 'quote' is populated in returned data.
		/// Returns either the following subset of that data (combined, one of the two is always empty):
		/// string companySymbol, string companyName, double quote, DateTime timeOfQuote
		/// or an error with the following information:
		/// long Code, string Message, string Status
		/// </summary>
		public async Task<IActionResult> Run(
			[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req)
		{
			// leaving this "printf" here so I can find it in Azure logging data
			this.mLogger.LogInformation("C# HTTP trigger function GetStockQuote processed a request.");

			TwelveDataQuote quote = null;
			TwelveDataRealTimeQuote quoteRealTime = null;
			bool getRealTimeQuote = false;
			string apiString;

			string apiKey = Environment.GetEnvironmentVariable("TWELVEDATA_API_KEY");
			string symbolToUse = req.Query["symbol"];
			if (symbolToUse == null)
			{
				return (ActionResult)new OkObjectResult(this.ErrorToReturn("Please provide a value for 'symbol' parameter.",
					"Request did not contain symbol to use for quote."));
			}

			string realTime = req.Query["realtime"];
			if (realTime != null && realTime == "1")
			{
				getRealTimeQuote = true;
				apiString = this.mTwelveDataRealTimeURL + HttpUtility.UrlEncode(apiKey) + "&symbol=" +
					HttpUtility.UrlEncode(symbolToUse);
			}
			else
			{
				apiString = this.mTwelveDataURL + HttpUtility.UrlEncode(apiKey) + "&symbol=" +
					HttpUtility.UrlEncode(symbolToUse);
			}

			try
			{
				HttpClient client = new HttpClient();
				HttpResponseMessage response = await client.GetAsync(apiString);
				if (response.IsSuccessStatusCode)
				{
					if (getRealTimeQuote) quoteRealTime = await response.Content.ReadAsAsync<TwelveDataRealTimeQuote>();
					else quote = await response.Content.ReadAsAsync<TwelveDataQuote>();

					if ((getRealTimeQuote && quoteRealTime.Price == null) || (!getRealTimeQuote && quote.Symbol == null))
					{
						// try to parse JSON again as an error response from TwelveData
						HttpResponseMessage response2 = await client.GetAsync(apiString);
						if (response2.IsSuccessStatusCode)
						{
							TwelveDataError error = await response2.Content.ReadAsAsync<TwelveDataError>();
							if (error.Status != null)
							{
								// this is a valid error message from TwelveData, so return it
								return (ActionResult)new OkObjectResult(this.ErrorToReturn(error));
							}
							else
							{
								// return unknown error to caller
								return (ActionResult)new OkObjectResult(this.ErrorToReturn("Unable to Process Request.",
									"JSON returned from TwelveData was not Quote as expected, nor was it Error."));
							}
						}
						else
						{
							// response2 code indicated HTTP failure
							return (ActionResult)new OkObjectResult(this.ErrorToReturn("Unable to Process Request. " +
								"HTTP Response Status Code: " + response2.StatusCode +
								"HTTP Response Message: " + response2.ReasonPhrase, ""));
						}
					}
				}
				else
				{
					// response code indicated HTTP failure
					return (ActionResult)new OkObjectResult(this.ErrorToReturn("Unable to Process Request. " +
						"HTTP Response Status Code: " + response.StatusCode +
						"HTTP Response Message: " + response.ReasonPhrase, ""));
				}

				if (getRealTimeQuote)
				{
					return (ActionResult)new OkObjectResult(this.MergeQuote(quoteRealTime));
				}
				else
				{
					return (ActionResult)new OkObjectResult(this.MergeQuote(quote));
				}
			}
			catch (System.Exception ex)
			{
				// return unknown error to caller
				return (ActionResult)new OkObjectResult(this.ErrorToReturn("Unable to Process Request.",
					"Exception caught trying to get or parse JSON data. Exception is:\n" + ex.ToString()));
			}
		}


		/// <summary>
		/// Populates ReturnedData with the real-time price and an empty error object.
		/// </summary>
		/// <param name="quote">Real-Time price is the only value in this object.</param>
		/// <returns>ReturnedData object ready to return to caller.</returns>
		private ReturnedData MergeQuote(TwelveDataRealTimeQuote quote)
		{
			ReturnedData returnValue;
			StockData data;
			ErrorData error = new ErrorData(0, "", "");

			data = new StockData("", "", double.Parse(quote.Price), DateTime.MinValue);

			returnValue = new ReturnedData(data, error, false);
			return returnValue;
		}


		/// <summary>
		/// Populates ReturnedData with an empty error object, and uses only the
		/// subset of data to be shown to the user.
		/// </summary>
		/// <param name="quote">Full JSON data.</param>
		/// <returns>ReturnedData object ready to return to caller.</returns>
		private ReturnedData MergeQuote(TwelveDataQuote quote)
		{
			ReturnedData returnValue;
			StockData data;
			DateTimeOffset dateTimeOffset;
			ErrorData error = new ErrorData(0, "", "");

			// timestamp from TwelveData is Epoch seconds (UTC)
			dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(quote.Timestamp);
			data = new StockData(quote.Symbol, quote.Name, double.Parse(quote.Close), dateTimeOffset.DateTime.ToLocalTime());

			returnValue = new ReturnedData(data, error, false);
			return returnValue;
		}


		/// <summary>
		/// Populates ReturnedData with an error object and empty data. Also logs internal error info. 
		/// </summary>
		/// <param name="aError">Error info from TwelveData.</param>
		/// <returns>ReturnedData object ready to return to caller.</returns>
		private ReturnedData ErrorToReturn(TwelveDataError aError)
		{
			StockData data = new StockData("", "", 0.0, DateTime.MinValue);
			ErrorData error = new ErrorData(aError.Code, aError.Message, aError.Status);
			ReturnedData returnValue = new ReturnedData(data, error, true);

			this.mLogger.LogInformation("C# HTTP trigger function GetStockQuote encountered an error from TwelveData. Error is:\n"
				+ aError.Message + "(Code: " + aError.Code + ", status: " + aError.Status);

			return returnValue;
		}


		/// <summary>
		/// Populates ReturnedData with an error object and empty data. Also logs internal error info. 
		/// </summary>
		/// <param name="message">Error message to return to caller.</param>
		/// <param name="messageToLog">Internal-use-only error message to log.</param>
		/// <returns>ReturnedData object ready to return to caller.</returns>
		private ReturnedData ErrorToReturn(string message, string messageToLog)
		{
			StockData data = new StockData("", "", 0.0, DateTime.MinValue);
			ErrorData error = new ErrorData(0, message, "");
			ReturnedData returnValue = new ReturnedData(data, error, true);

			if (string.IsNullOrEmpty(messageToLog))
			{
				messageToLog = message;
			}

			this.mLogger.LogInformation("C# HTTP trigger function GetStockQuote encountered an unexpected error. Error is:\n"
				+ messageToLog ?? message);

			return returnValue;
		}
	}
}
