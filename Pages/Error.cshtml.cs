using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Diagnostics;

namespace UtilizationPage_ASP.Pages
{
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [IgnoreAntiforgeryToken]
    public class ErrorModel : PageModel
    {
        public string? RequestId { get; set; }
        public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
        public string? ErrorMessage { get; set; }
        public int? StatusCode { get; set; }

        private readonly ILogger<ErrorModel> _logger;

        public ErrorModel(ILogger<ErrorModel> logger)
        {
            _logger = logger;
        }

        public void OnGet(int? statusCode = null)
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
            StatusCode = statusCode;
            
            if (statusCode.HasValue)
            {
                ErrorMessage = statusCode switch
                {
                    404 => "The page you're looking for doesn't exist.",
                    500 => "We're experiencing some technical difficulties.",
                    _ => "An error occurred while processing your request."
                };
            }

            _logger.LogError($"Error {statusCode}: {ErrorMessage} - Request ID: {RequestId}");
        }
    }
}
