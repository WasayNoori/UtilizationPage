using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Threading.Tasks;
using UtilizationPage_ASP.Services;

namespace UtilizationPage_ASP.Pages
{
    public class FeedbackModel : PageModel
    {
        private readonly EntryService _entryService;
        private readonly ILogger<FeedbackModel> _logger;

        public FeedbackModel(EntryService entryService, ILogger<FeedbackModel> logger)
        {
            _entryService = entryService;
            _logger = logger;
        }

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostAddReviewAsync([FromBody] ReviewRequest request)
        {
            try
            {
                var userEmail = User.Identity.Name;
                if (string.IsNullOrEmpty(userEmail))
                {
                    return BadRequest("User not authenticated");
                }

                await _entryService.AddReview(userEmail, request.Stars, request.Comments);
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error adding review: {ex.Message}");
                return BadRequest("Error submitting feedback");
            }
        }
    }

    public class ReviewRequest
    {
        public int Stars { get; set; }
        public string Comments { get; set; }
    }
} 