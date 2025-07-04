using Microsoft.AspNetCore.Mvc.RazorPages;
using UtilizationPage_ASP.Services;
using System.Collections.Generic;
using System.Threading.Tasks;

public class ReviewDashboardModel : PageModel
{
    private readonly EntryService _entryService;

    public List<ReviewModel> Reviews { get; set; }

    public ReviewDashboardModel(EntryService entryService)
    {
        _entryService = entryService;
    }

    public async Task OnGetAsync()
    {
        Reviews = await _entryService.GetReviews();
    }
}

