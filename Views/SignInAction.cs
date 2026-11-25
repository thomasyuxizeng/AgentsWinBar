using CodexBarWin.Models;
using CodexBarWin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Views;

/// <summary>What every "Sign in" button does: watch for the new sign-in, then open the provider's own sign-in.</summary>
internal static class SignInAction
{
    public static void Start(string provider)
    {
        var logger = App.Services.GetRequiredService<ILogger<SignInLauncher>>();
        try
        {
            var id = ProviderConstants.ValidateAndNormalize(provider);
            var watcher = App.Services.GetRequiredService<ISignInWatcher>();
            // Watch first so a sign-in the tool writes right away is not missed
            watcher.Begin(id);
            if (!App.Services.GetRequiredService<ISignInLauncher>().Launch(id).Started) watcher.Cancel(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sign-in for {Provider} could not be opened", provider);
        }
    }
}
