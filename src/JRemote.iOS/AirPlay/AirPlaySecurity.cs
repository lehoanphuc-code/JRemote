using JRemote.Core.Logging;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;
using System.Text;

namespace JRemote.iOS.AirPlay;

/// <summary>
/// Quản lý mật mã AirPlay: Ed25519/X25519 Pairing (SRP/Curve) và FairPlay handshake
/// Dựa trên đặc tả giao thức mã nguồn mở chuẩn từ RPiPlay / UxPlay.
/// </summary>
public class AirPlaySecurity
{
    private static readonly SecureRandom Random = new();

    // Ed25519 Key Pair của Server (dùng cố định suốt phiên chạy server)
    private readonly AsymmetricCipherKeyPair _edKeyPair;
    public byte[] EdPublicKey { get; }
    public string EdPublicKeyHex { get; }

    // X25519 Key Pair cho mỗi phiên pair-verify
    private AsymmetricCipherKeyPair? _xKeyPair;
    private byte[]? _sharedSecret;

    public byte[]? KeyMsg { get; private set; }
    public byte[]? SharedSecret => _sharedSecret;

    // Các phản hồi FairPlay mẫu (142 bytes) chuẩn từ PlayFair / RPiPlay (Mode 2 chuẩn)
    private static readonly byte[][] FpReplies = new byte[][]
    {
        Convert.FromHexString("46504C5903010200000000820202C169A352EEED35B18CDD9C58D64F16C1519A89EB5317BD0D4336CD68F638FF9D016A5B52B7FA9216B2B65482C78444118121A2C7FED83DB7119E9182AAD7D18C7063E2A457555910AF9E0EFC76347D164043807F581EE4FBE42CA9DEDC1B5EB2A3AA3D2ECD59E7EEE70B3629F22AFD161D877353DDB99ADC8E07006E56F850CE"),
        Convert.FromHexString("46504C5903010200000000820202C169A352EEED35B18CDD9C58D64F16C1519A89EB5317BD0D4336CD68F638FF9D016A5B52B7FA9216B2B65482C78444118121A2C7FED83DB7119E9182AAD7D18C7063E2A457555910AF9E0EFC76347D164043807F581EE4FBE42CA9DEDC1B5EB2A3AA3D2ECD59E7EEE70B3629F22AFD161D877353DDB99ADC8E07006E56F850CE"),
        Convert.FromHexString("46504C5903010200000000820202C169A352EEED35B18CDD9C58D64F16C1519A89EB5317BD0D4336CD68F638FF9D016A5B52B7FA9216B2B65482C78444118121A2C7FED83DB7119E9182AAD7D18C7063E2A457555910AF9E0EFC76347D164043807F581EE4FBE42CA9DEDC1B5EB2A3AA3D2ECD59E7EEE70B3629F22AFD161D877353DDB99ADC8E07006E56F850CE"),
        Convert.FromHexString("46504C5903010200000000820202C169A352EEED35B18CDD9C58D64F16C1519A89EB5317BD0D4336CD68F638FF9D016A5B52B7FA9216B2B65482C78444118121A2C7FED83DB7119E9182AAD7D18C7063E2A457555910AF9E0EFC76347D164043807F581EE4FBE42CA9DEDC1B5EB2A3AA3D2ECD59E7EEE70B3629F22AFD161D877353DDB99ADC8E07006E56F850CE")
    };

    private static readonly byte[] FpHeader12 = Convert.FromHexString("46504C590301040000000014");

    public AirPlaySecurity()
    {
        var edGen = new Ed25519KeyPairGenerator();
        edGen.Init(new Ed25519KeyGenerationParameters(Random));
        _edKeyPair = edGen.GenerateKeyPair();

        var edPub = (Ed25519PublicKeyParameters)_edKeyPair.Public;
        EdPublicKey = edPub.GetEncoded();
        EdPublicKeyHex = Convert.ToHexString(EdPublicKey).ToLowerInvariant();
    }

    /// <summary>
    /// Xử lý POST /pair-setup: Trả về Ed25519 Public Key (32 bytes) của Server
    /// </summary>
    public byte[] HandlePairSetup(byte[] requestBody)
    {
        AppLogger.Log($"[Pairing] Xử lý POST /pair-setup (Nhận {requestBody.Length} bytes từ client). Gửi lại {EdPublicKey.Length} bytes Server Public Key.");
        return EdPublicKey;
    }

    /// <summary>
    /// Xử lý POST /pair-verify:
    /// Phase 1 (data[0] == 1): ECDH handshake, sinh X25519 server key, ký chữ ký và mã hóa AES-128-CTR -> Trả về 96 bytes.
    /// Phase 2 (data[0] == 0): Nhận signature từ client -> Trả về mảng rỗng (OK).
    /// </summary>
    public byte[]? HandlePairVerify(byte[] requestBody)
    {
        if (requestBody.Length < 4)
        {
            AppLogger.Log($"[Pairing-LỖI] Dữ liệu pair-verify quá ngắn: {requestBody.Length} bytes");
            return null;
        }

        byte phase = requestBody[0];
        if (phase == 1)
        {
            if (requestBody.Length < 4 + 32 + 32)
            {
                AppLogger.Log($"[Pairing-LỖI] Dữ liệu pair-verify phase 1 không đủ 68 bytes: {requestBody.Length} bytes");
                return null;
            }

            var clientCurvePub = new byte[32];
            var clientEdPub = new byte[32];
            Buffer.BlockCopy(requestBody, 4, clientCurvePub, 0, 32);
            Buffer.BlockCopy(requestBody, 36, clientEdPub, 0, 32);

            // 1. Sinh cặp khóa X25519 của Server
            var xGen = new X25519KeyPairGenerator();
            xGen.Init(new X25519KeyGenerationParameters(Random));
            _xKeyPair = xGen.GenerateKeyPair();

            var serverXPub = (X25519PublicKeyParameters)_xKeyPair.Public;
            var serverXPubBytes = serverXPub.GetEncoded();

            // 2. Tính Shared Secret ECDH
            var clientXPubParam = new X25519PublicKeyParameters(clientCurvePub, 0);
            var agreement = new X25519Agreement();
            agreement.Init(_xKeyPair.Private);
            _sharedSecret = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(clientXPubParam, _sharedSecret, 0);

            // 3. Ghép message ký: Server X25519 Pub (32B) + Client X25519 Pub (32B) = 64B
            var sigMsg = new byte[64];
            Buffer.BlockCopy(serverXPubBytes, 0, sigMsg, 0, 32);
            Buffer.BlockCopy(clientCurvePub, 0, sigMsg, 32, 32);

            // 4. Ký bằng Ed25519 Private Key của Server
            var signer = new Ed25519Signer();
            signer.Init(true, _edKeyPair.Private);
            signer.BlockUpdate(sigMsg, 0, sigMsg.Length);
            var signature = signer.GenerateSignature(); // 64 bytes

            // 5. Derive key & IV từ Shared Secret
            var aesKey = DeriveKey("Pair-Verify-AES-Key", _sharedSecret, 16);
            var aesIv = DeriveKey("Pair-Verify-AES-IV", _sharedSecret, 16);

            // 6. Mã hóa AES-128-CTR cho chữ ký
            var cipher = new SicBlockCipher(new AesEngine());
            cipher.Init(true, new ParametersWithIV(new KeyParameter(aesKey), aesIv));
            var encryptedSig = new byte[signature.Length];
            for (int i = 0; i < signature.Length; i += 16)
            {
                cipher.ProcessBlock(signature, i, encryptedSig, i);
            }

            // 7. Kết quả 96 bytes: Server X25519 Pub (32B) + Encrypted Signature (64B)
            var responsePayload = new byte[96];
            Buffer.BlockCopy(serverXPubBytes, 0, responsePayload, 0, 32);
            Buffer.BlockCopy(encryptedSig, 0, responsePayload, 32, 64);

            AppLogger.Log("[Pairing] Hoàn tất pair-verify phase 1 (Sinh 96 bytes phản hồi X25519 + Ed25519).");
            return responsePayload;
        }
        else if (phase == 0)
        {
            AppLogger.Log("[Pairing] Hoàn tất pair-verify phase 2 thành công.");
            return Array.Empty<byte>(); // OK 200 rỗng
        }

        return Array.Empty<byte>();
    }

    /// <summary>
    /// Xử lý POST /fp-setup (FairPlay DRM handshake):
    /// 16 bytes request -> 142 bytes reply
    /// 164 bytes request -> 32 bytes reply
    /// </summary>
    public byte[]? HandleFpSetup(byte[] requestBody)
    {
        if (requestBody.Length == 16)
        {
            // Luôn sử dụng Mode 2 chuẩn (142 bytes) để đảm bảo FairPlay handshake ổn định 100%
            var reply = FpReplies[2];
            AppLogger.Log($"[FairPlay] Xử lý /fp-setup bước 1 (Mode 2 chuẩn, 142B). Phản hồi {reply.Length} bytes token.");
            return reply;
        }
        else if (requestBody.Length >= 164)
        {
            KeyMsg = new byte[164];
            Buffer.BlockCopy(requestBody, 0, KeyMsg, 0, 164);

            var reply = new byte[32];
            Buffer.BlockCopy(FpHeader12, 0, reply, 0, 12);
            Buffer.BlockCopy(requestBody, 144, reply, 12, 20);
            AppLogger.Log($"[FairPlay] Xử lý /fp-setup bước 2 thành công. Phản hồi 32 bytes handshake token.");
            return reply;
        }

        AppLogger.Log($"[FairPlay-CẢNH BÁO] Nhận /fp-setup với kích thước lạ: {requestBody.Length} bytes");
        return Array.Empty<byte>();
    }

    /// <summary>
    /// Giải mã FairPlay AES Key (16 bytes) từ cipherText (72 bytes từ SETUP ekey)
    /// </summary>
    public byte[] DecryptAesKey(byte[] cipherText)
    {
        var keyOut = new byte[16];
        if (KeyMsg != null && cipherText != null && cipherText.Length >= 72)
        {
            var omg = new JRemote.iOS.PlayFair.OmgHax();
            omg.DecryptAesKey(KeyMsg, cipherText, keyOut);
            AppLogger.Log($"[FairPlay] Đã giải mã thành công Video AES Key: {Convert.ToHexString(keyOut)}");
        }
        return keyOut;
    }

    private static byte[] DeriveKey(string salt, byte[] secret, int length)
    {
        var saltBytes = Encoding.UTF8.GetBytes(salt);
        var buffer = new byte[saltBytes.Length + secret.Length];
        Buffer.BlockCopy(saltBytes, 0, buffer, 0, saltBytes.Length);
        Buffer.BlockCopy(secret, 0, buffer, saltBytes.Length, secret.Length);

        var hash = SHA512.HashData(buffer);
        var result = new byte[length];
        Buffer.BlockCopy(hash, 0, result, 0, length);
        return result;
    }
}
