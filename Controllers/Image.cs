using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace cherished_server.Controllers
{
    public class ImageController : Controller
    {
        [HttpGet]
        [Route("image/{imageName}")]
        public IActionResult GetImage(string imageName)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", imageName);

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound();
            }

            var image = System.IO.File.ReadAllBytes(filePath);
            return File(image, "image/jpeg");
        }
    }
}
