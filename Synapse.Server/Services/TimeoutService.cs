using Microsoft.Extensions.Logging;

namespace Synapse.Server.Services;

public interface ITimeoutService
{
    public void Timeout(Action action, int milliseconds);
}

public class TimeoutService(ILogger<RoleService> log) : ITimeoutService
{
    public bool? _doTimeout;

    public void Timeout(Action action, int milliseconds)
    {
        if (_doTimeout.HasValue)
        {
            _doTimeout = true;
            return;
        }

        _doTimeout = false;
        try
        {
            action();
            _ = TimeoutTask(action, milliseconds);
        }
        catch (Exception e)
        {
            log.LogCritical(e, "An exception occurred while trying to start timeout");
        }
    }

    private async Task TimeoutTask(Action action, int milliseconds)
    {
        await Task.Delay(milliseconds);
        if (_doTimeout == true)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                log.LogCritical(e, "An exception occurred while trying to finish timeout");
            }
        }

        _doTimeout = null;
    }
}
