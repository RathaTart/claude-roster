namespace ClaudeRoster.Core;

/// <summary>A failure whose message is safe and useful to show the user as-is.</summary>
public class ClaudeRosterException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Claude is signed in to an account the roster doesn't know, so switching away would lose it.</summary>
public sealed class UnsavedAccountException()
    : ClaudeRosterException(
        "The account signed in to Claude isn't saved in the roster yet. Save it first so you can switch back to it.");
