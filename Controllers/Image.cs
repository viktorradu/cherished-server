using Microsoft.AspNetCore.Mvc;

namespace cherished_server.Services
{
    [ApiController]
    public class Image : ControllerBase
    {
        [HttpGet]
        public IActionResult GetImage([FromQuery] string size, [FromQuery] int key)
        {
            var image = System.IO.File.OpenRead("F:\\Data\\Pictures\\2024\\Halloween\\FB_IMG_1728821589567.jpg"); 
            return File(image, "image/jpeg"); }
        }
}
