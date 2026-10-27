using System.Globalization;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Ai.Tlbx.VoiceAssistant.Telnyx;

public sealed class TelnyxSignatureVerifier(TelnyxOptions options, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Verify timestamp|raw JSON body for HTTP, or timestamp|exact configured stream URL for a WebSocket upgrade.</summary>
    public bool Verify(string timestamp, string signature, string bodyOrStreamUrl)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) ||
            seconds < 0 || Math.Abs(_clock.GetUtcNow().ToUnixTimeSeconds() - (double)seconds) > options.SignatureTolerance.TotalSeconds)
            return false;
        try
        {
            var key = Convert.FromBase64String(options.PublicKey);
            var bytes = Convert.FromBase64String(signature);
            if (key.Length != 32 || bytes.Length != 64) return false;
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
            var message = Encoding.UTF8.GetBytes(timestamp + "|" + bodyOrStreamUrl);
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(bytes);
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
