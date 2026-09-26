using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace TrenchHQ.Helpers
{
    internal enum StartupRegistrationAvailability
    {
        Available,
        DisabledByUser,
        DisabledByPolicy,
        Unavailable
    }

    internal readonly record struct StartupRegistrationStatus(
        bool IsEnabled,
        StartupRegistrationAvailability Availability);

    internal static class StartupRegistrationService
    {
        internal const string TaskId = "TrenchHQStartupTask";

        internal static async Task<StartupRegistrationStatus> GetStatusAsync()
        {
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                return FromState(task.State);
            }
            catch
            {
                return new StartupRegistrationStatus(false, StartupRegistrationAvailability.Unavailable);
            }
        }

        internal static async Task<StartupRegistrationStatus> SetEnabledAsync(bool enabled)
        {
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (!enabled)
                {
                    if (task.State == StartupTaskState.Enabled)
                    {
                        task.Disable();
                    }

                    return FromState(task.State);
                }

                if (task.State == StartupTaskState.Disabled)
                {
                    await task.RequestEnableAsync();
                }

                return FromState(task.State);
            }
            catch
            {
                return new StartupRegistrationStatus(false, StartupRegistrationAvailability.Unavailable);
            }
        }

        private static StartupRegistrationStatus FromState(StartupTaskState state)
        {
            return state switch
            {
                StartupTaskState.Enabled => new StartupRegistrationStatus(true, StartupRegistrationAvailability.Available),
                StartupTaskState.DisabledByUser => new StartupRegistrationStatus(false, StartupRegistrationAvailability.DisabledByUser),
                StartupTaskState.DisabledByPolicy => new StartupRegistrationStatus(false, StartupRegistrationAvailability.DisabledByPolicy),
                StartupTaskState.EnabledByPolicy => new StartupRegistrationStatus(true, StartupRegistrationAvailability.DisabledByPolicy),
                _ => new StartupRegistrationStatus(false, StartupRegistrationAvailability.Available)
            };
        }
    }
}
