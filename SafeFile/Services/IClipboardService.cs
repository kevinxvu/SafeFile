using System.Threading.Tasks;

namespace SafeFile.Services;

public interface IClipboardService
{
    Task SetTextAsync(string text);
}
