using Microsoft.AspNetCore.Mvc;
using ExifLibrary;
using System.IO;
using System.Text;

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
            var path_label = _Pool.GetFileLocationFromPath(Path.GetDirectoryName(path) ?? "");
            var imageFile = ImageFile.FromFile(path);
            imageFile.Properties.Add(new ExifAscii(ExifTag.ImageDescription, path_label, Encoding.UTF8));

            var stream = new MemoryStream();
            imageFile.Save(stream);
            stream.Position = 0;
            return File(stream, "image/jpeg");
        }
        
        [HttpGet]
        [Route("loadpool")]
        public IActionResult LoadPool()
        {
            return Ok(new {PoolSize = _Pool.PoolSize, SlideshowIntervalMs = _Pool.SlideshowIntervalMs});
        }
    }
}