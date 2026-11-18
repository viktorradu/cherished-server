using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace cherished_server.Services
{
    [ApiController]
    [Route("image")]
    public class ImageController : ControllerBase
    {
        private readonly Pool _pool;
        private readonly ILogger<Pool> _logger;
        private static readonly FileExtensionContentTypeProvider ContentTypes = new();

        public ImageController(Pool pool, ILogger<Pool> logger)
        {
            _pool = pool;
            _logger = logger;
        }

        [HttpGet("")]
        public IActionResult GetImage([FromQuery] string key)
        {
            if (!_pool.Snapshot.IsReady)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Pool is not ready." });
            var path = _pool.GetFilePath(key);
            if (path is null)
                return NotFound(new { error = "Image is unavailable." });
            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                ContentTypes.TryGetContentType(path, out var contentType);
                return File(stream, contentType ?? "application/octet-stream");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _logger.LogWarning(error, "Image {Id} could not be opened.", key);
                return NotFound(new { error = "Image is unavailable." });
            }
        }

        [HttpPost("hide")]
        public IActionResult SetHidden([FromQuery] string key) => Mark(key, true);

        [HttpPost("flag")]
        public IActionResult SetFlagged([FromQuery] string key) => Mark(key, false);

        private IActionResult Mark(string key, bool hide)
        {
            if (!_pool.Snapshot.IsReady)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Pool is not ready." });
            try
            {
                return (hide ? _pool.SetFileHidden(key) : _pool.SetFileFlagged(key))
                    ? Ok() : NotFound(new { error = "Image is unavailable." });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _logger.LogError(error, "Could not persist {Action} for image {Id}.", hide ? "hide" : "flag", key);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Could not save image action." });
            }
        }

        [HttpGet("loadpool")]
        public IActionResult LoadPool()
        {
            var snapshot = _pool.Snapshot;
            if (!snapshot.IsReady)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Pool is not ready." });
            return Ok(new { PoolSize = snapshot.Images.Count, _pool.SlideshowIntervalMs, Images = snapshot.Images });
        }
    }
}
