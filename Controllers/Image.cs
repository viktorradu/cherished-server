using Microsoft.AspNetCore.Mvc;

namespace cherished_server.Services
{
    [ApiController]
    public class Image
    {
        [HttpGet]
        public IActionResult GetImage([FromQuery] string size, [FromQuery] int key)
        {
            var image = System.IO.File.OpenRead(imagePath); 
            return new File(image, "image/jpeg"); }
        }
}
