namespace PlayerService.Abstractions.Errors;

/// <summary>Reasons a login was refused by the device grain.</summary>
[GenerateSerializer, Alias("login-rejection")]
public enum LoginRejection
{
    /// <summary>This deviceId already holds a live session (plan §5.6) — HTTP 409.</summary>
    DeviceSessionActive = 0,
}
