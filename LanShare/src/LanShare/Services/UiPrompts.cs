using Avalonia.Threading;
using LanShare.Core;
using LanShare.Views;

namespace LanShare.Services;

public sealed class UiPrompts(Notifier notifier) : IPairingUi
{
    public Task<bool> ConfirmAsync(PairingRequest r, CancellationToken ct)
    {
        notifier.Show(
            "Pairing request",
            $"“{r.PeerName}” wants to connect. Compare the code on both screens."
        );
        return Dispatcher.UIThread.InvokeAsync(() =>
            PromptWindow.ShowAsync(
                "Pair with a new device",
                $"“{r.PeerName}” ({r.PeerAddress}) wants to connect",
                "Check that this code is IDENTICAL on both devices before you continue. If it differs, press Cancel: someone may be intercepting the connection.",
                r.Code,
                "The codes match",
                "Cancel",
                ct
            )
        );
    }
}
