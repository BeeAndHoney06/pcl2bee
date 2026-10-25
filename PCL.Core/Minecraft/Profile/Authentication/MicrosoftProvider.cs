using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.Http;
using PCL.Core.Minecraft.IdentityModel.OAuth;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile.Authentication.Utils;
using PCL.Core.Minecraft.Profile.Authentication.Utils.Models;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL.Core.Minecraft.Profile.Authentication;

/// <summary>
/// Microsoft consumer account provider. OAuth, Xbox Live, XSTS and Minecraft service calls
/// are kept here so launch code never needs to understand the six-step protocol.
/// </summary>
public sealed class MicrosoftProvider : IAuthenticateProvider
{
    private const string DeviceEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    // 旧版 Microsoft 账户（MSA）公共客户端。使用 Live Connect 设备代码流，无需 Azure 应用注册。
    private const string LiveDeviceEndpoint = "https://login.live.com/oauth20_connect.srf";
    private const string LiveTokenEndpoint = "https://login.live.com/oauth20_token.srf";
    private const string EntraScope = "XboxLive.signin offline_access";
    private const string LiveScope = "service::user.auth.xboxlive.com::MBI_SSL";

    /// <summary>
    ///     沿用自 PCL CE 的 Azure 公共客户端 ID（公开标识，不是机密），用于 Entra 设备代码流。
    /// </summary>
    public const string PclCeClientId = "c14b0370-8d75-42f8-b329-5b60d39e319f";

    /// <summary>
    ///     旧版 MSA 公共客户端 ID（Microsoft 官方 Xbox 应用 ID，非机密）。
    /// </summary>
    public const string LegacyMsaClientId = "00000000402b5328";

    private readonly string _clientId;

    public MicrosoftProvider(string? clientId = null)
    {
        _clientId = !string.IsNullOrWhiteSpace(clientId)
            ? clientId
            : string.IsNullOrWhiteSpace(Secrets.MSOAuthClientId)
                ? PclCeClientId
                : Secrets.MSOAuthClientId;
    }

    /// <summary>
    ///     16 位十六进制 ID 视为旧版 MSA 客户端，其余（Azure GUID）使用 Entra 设备代码流。
    /// </summary>
    private bool UseLiveConnect => IsLegacyMsaClientId(_clientId);

    private static bool IsLegacyMsaClientId(string id)
    {
        if (id.Length != 16) return false;
        foreach (var c in id)
            if (!Uri.IsHexDigit(c))
                return false;
        return true;
    }

    public async Task<DeviceCodeData> GetDeviceCodeAsync(CancellationToken token)
    {
        var fields = new Dictionary<string, string> { ["client_id"] = _clientId };
        string endpoint;
        if (UseLiveConnect)
        {
            endpoint = LiveDeviceEndpoint;
            fields["scope"] = LiveScope;
            fields["response_type"] = "device_code";
        }
        else
        {
            endpoint = DeviceEndpoint;
            fields["scope"] = EntraScope;
        }

        using var response = await HttpRequest.CreatePost(endpoint)
            .WithContent(new FormUrlEncodedContent(fields))
            .SendAsync(NetworkService.GetClient(NetworkService.MicrosoftEntraId), cancellationToken: token)
            .ConfigureAwait(false);
        var result = await response.AsJsonAsync<DeviceCodeData>(cancellationToken: token).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Microsoft device authorization response was empty.");
        if (result.IsError) throw new IdentityModelAuthenticationException(result.Error, result.ErrorDescription);
        result.Validate();
        return result;
    }

    public async Task<AuthorizeResult?> PollDeviceCodeAsync(DeviceCodeData data, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(data);
        var endpoint = UseLiveConnect ? LiveTokenEndpoint : TokenEndpoint;
        using var response = await HttpRequest.CreatePost(endpoint)
            .WithContent(new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = data.DeviceCode ?? string.Empty
            }))
            .SendAsync(NetworkService.GetClient(NetworkService.MicrosoftEntraId), cancellationToken: token)
            .ConfigureAwait(false);
        var result = await response.AsJsonAsync<AuthorizeResult>(cancellationToken: token).ConfigureAwait(false);
        result?.Validate();
        return result;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var oauth = request.OAuthResult;
        if (oauth is null && !string.IsNullOrWhiteSpace(request.RefreshToken))
            oauth = await RefreshOAuthAsync(request.RefreshToken, token).ConfigureAwait(false);
        if (oauth is null && request.DeviceCodeHandler is not null)
        {
            var device = await GetDeviceCodeAsync(token).ConfigureAwait(false);
            oauth = await request.DeviceCodeHandler(
                new DeviceCodeAuthenticationContext(device, pollToken => PollDeviceCodeAsync(device, pollToken)), token)
                .ConfigureAwait(false);
        }
        if (oauth is null)
            throw new IdentityModelConfigurationException("Microsoft login requires an OAuth result or device-code handler.");
        return await CompleteAsync(oauth, token).ConfigureAwait(false);
    }

    public async Task<AuthenticationResult> RefreshAsync(McProfile profile, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(profile.RefreshToken))
            throw new IdentityModelAuthenticationException("invalid_grant", "Microsoft refresh token is empty.");
        return await AuthenticateAsync(new AuthenticationRequest { RefreshToken = profile.RefreshToken }, token)
            .ConfigureAwait(false);
    }

    public async Task<bool> ValidateAsync(McProfile profile, CancellationToken token)
    {
        if (profile.IsExpired || string.IsNullOrWhiteSpace(profile.AccessToken)) return false;
        return await MojangUtils.CheckLicenseAsync(profile, token).ConfigureAwait(false);
    }

    public async Task<AuthorizeResult> RefreshOAuthAsync(string refreshToken, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var endpoint = UseLiveConnect ? LiveTokenEndpoint : TokenEndpoint;
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = UseLiveConnect ? LiveScope : EntraScope
        };
        using var response = await HttpRequest.CreatePost(endpoint)
            .WithContent(new FormUrlEncodedContent(fields))
            .SendAsync(NetworkService.GetClient(NetworkService.MicrosoftEntraId), cancellationToken: token)
            .ConfigureAwait(false);
        var result = await response.AsJsonAsync<AuthorizeResult>(cancellationToken: token).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Microsoft token response was empty.");
        if (result.IsError) throw new IdentityModelAuthenticationException(result.Error, result.ErrorDescription);
        result.Validate();
        return result with { RefreshToken = result.RefreshToken ?? refreshToken };
    }

    public async Task<AuthenticationResult> CompleteAsync(AuthorizeResult oauth, CancellationToken token)
    {
        if (oauth.IsError) throw new IdentityModelAuthenticationException(oauth.Error, oauth.ErrorDescription);
        if (string.IsNullOrWhiteSpace(oauth.AccessToken))
            throw new IdentityModelAuthenticationException("invalid_token", "Microsoft OAuth did not return an access token.");

        var xbl = await XboxUtils.AuthenticateAsync(new XboxAuthenticate<XboxProperty>
        {
            Properties = new XboxProperty { RpsTicket = $"d={oauth.AccessToken}" },
            TokenType = "JWT"
        }, isXsts: false, token).ConfigureAwait(false)
                  ?? throw new IdentityModelAuthenticationException("xbox_authentication_failed", "Xbox Live returned no token.");

        var xsts = await XboxUtils.AuthenticateAsync(new XboxAuthenticate<XSTSProperty>
        {
            Properties = new XSTSProperty { UserTokens = [xbl.Token] },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        }, isXsts: true, token).ConfigureAwait(false)
                   ?? throw new IdentityModelAuthenticationException("xsts_authentication_failed", "XSTS returned no token.");

        var userHash = xsts.DisplayClaims?.Xui is { Count: > 0 } ? xsts.DisplayClaims.Xui[0].UserHash : null;
        if (string.IsNullOrWhiteSpace(userHash))
            throw new IdentityModelAuthenticationException("xsts_missing_user_hash", "XSTS response did not contain a user hash.");

        var minecraft = await MojangUtils.AuthenticateAsync(xsts.Token, userHash, token).ConfigureAwait(false)
                        ?? throw new IdentityModelAuthenticationException("minecraft_authentication_failed", "Minecraft authentication returned no token.");
        if (string.IsNullOrWhiteSpace(minecraft.AccessToken))
            throw new IdentityModelAuthenticationException("minecraft_authentication_failed", "Minecraft access token is empty.");

        var safe = new SafeProfile
        {
            UserName = minecraft.UserName,
            AccessToken = minecraft.AccessToken,
            Uuid = string.Empty,
            TokenType = minecraft.TokenType
        };
        if (!await MojangUtils.CheckLicenseAsync(safe, token).ConfigureAwait(false))
            throw new IdentityModelAuthenticationException("minecraft_not_owned", "The Microsoft account does not own Minecraft.");

        var profile = await MojangUtils.GetProfileAsync(safe, token).ConfigureAwait(false);
        return new AuthenticationResult
        {
            ProfileType = ProfileType.Microsoft,
            UserName = profile.Name,
            Uuid = profile.Uuid,
            AccessToken = minecraft.AccessToken,
            RefreshToken = oauth.RefreshToken ?? string.Empty,
            ClientToken = profile.Uuid,
            TokenType = minecraft.TokenType,
            ExpiresAt = minecraft.ExpiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(minecraft.ExpiresIn) : null,
            RawJson = System.Text.Json.JsonSerializer.Serialize(profile),
            Provider = "microsoft"
        };
    }
}
