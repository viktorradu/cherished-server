using Microsoft.AspNetCore.Mvc;

namespace cherished_server.Services
{
    [ApiController]
    [Route("image")]
    public class ImageController : ControllerBase
    {
        private readonly Pool _Pool;

        public ImageController(Pool pool)
        {
            _Pool = pool;
        }
        [HttpGet] 
        [Route("")]
        public IActionResult GetImage([FromQuery] int key)
        {
            var path = _Pool.GetFilePath(key);
            var image = System.IO.File.OpenRead(path); 
            return File(image, "image/jpeg"); }
        }
}
