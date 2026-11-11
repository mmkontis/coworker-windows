namespace Coworker.Services;

// Cross-page events, sent with WeakReferenceMessenger.Default (Mac: NotificationCenter).

/// The dictionary changed (add/edit/remove, quick-add); listeners reload it.
public sealed record DictionaryChangedMessage;

/// Productivity stats changed (a dictation finished, typing speed edited); listeners refresh them.
public sealed record StatsChangedMessage;

/// Ask the shell to show Settings.
public sealed record OpenSettingsMessage;

/// The session is gone (token rejected or refresh refused); the shell returns to sign-in.
public sealed record SessionInvalidatedMessage(string Reason);
