using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace cherished_server.Pages
{
    public class ManagerModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;

        public ManagerModel(ILogger<IndexModel> logger)
        {
            _logger = logger;
        }
    }
}
