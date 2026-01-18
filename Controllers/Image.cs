using Microsoft.AspNetCore.Mvc;
using ExifLibrary;
using System.IO;
using System.Text;
using System.Buffers.Text;
using System.Text.Encodings.Web;
using System.Web;

namespace cherished_server.Services
{
    [ApiController]
    [Route("image")]
    public class ImageController : ControllerBase
    {
        private readonly Pool _Pool;
        public ImageController(Pool pool, ILogger<Pool> logger)
        {
            _Pool = pool;
        }

        [HttpGet]
        [Route("")]
        public IActionResult GetImage([FromQuery] int key)
        {
            var path = _Pool.GetFilePath(key);
            var hiddenFiles = _Pool.GetHiddenFiles();
            if (hiddenFiles.Contains(path))
            {
                return new EmptyResult();
            }
            var path_label = _Pool.GetFileLocationFromPath(Path.GetDirectoryName(path) ?? "");
            var safe_label = HttpUtility.UrlPathEncode(path_label);
            var imageFile = ImageFile.FromFile(path);
            var existingProperty = imageFile.Properties.Where(p=>p.Tag == ExifTag.ImageDescription).FirstOrDefault();
            if(existingProperty != null){
                existingProperty.Value = safe_label;
            } 
            else{
                var property = new ExifAscii(ExifTag.ImageDescription, safe_label, Encoding.UTF8);
                imageFile.Properties.Add(property);
            }

            var stream = new MemoryStream();
            imageFile.Save(stream);
            stream.Position = 0;

            return File(stream, "image/jpeg");
        }

        [HttpPost]
        [Route("hide")]
        public IActionResult SetHidden([FromQuery] int key)
        {
            _Pool.SetFileHidden(key);
            return Ok();
        }

        [HttpPost]
        [Route("flag")]
        public IActionResult SetFlagged([FromQuery] int key)
        {
            _Pool.SetFileFlagged(key);
            return Ok();
        }
        
        [HttpGet]
        [Route("loadpool")]
        public IActionResult LoadPool()
        {
            return Ok(new {PoolSize = _Pool.PoolSize, SlideshowIntervalMs = _Pool.SlideshowIntervalMs});
        }
    }
}