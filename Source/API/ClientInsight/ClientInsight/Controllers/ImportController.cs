namespace ClientInsightAPI.Controllers
{
    using ClientInsightAPI.Services;
    using Microsoft.AspNetCore.Mvc;

    [ApiController]
    [Route("api/import")]
    public class ImportController : ControllerBase
    {
        private readonly ImportService _import;

        public ImportController(ImportService import) => _import = import;

        [HttpPost]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> Import([FromQuery] string mode, IFormFile file, CancellationToken ct)
        {
            if (file is null || file.Length == 0) return BadRequest("File is required.");
            var importMode = mode?.ToLowerInvariant() == "replace" ? ImportMode.Replace : ImportMode.Update;

            var result = await _import.ImportJsonAsync(file, importMode, ct);
            return Ok(result);
        }
    }

    public enum ImportMode { Update, Replace }

}
