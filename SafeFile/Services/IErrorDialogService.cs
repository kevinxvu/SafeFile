using System.Threading.Tasks;

namespace SafeFile.Services;

public interface IErrorDialogService
{
    Task ShowErrorAsync(string message, string title = "Đã xảy ra lỗi");
}
