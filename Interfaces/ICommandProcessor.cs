using System;
using System.Threading.Tasks;

namespace StockNotificationApi.Interfaces
{
    public interface ICommandProcessor
    {
        Task EnqueueAsync(long chatId, Func<Task> work);
        Task StopAsync();
    }
}
