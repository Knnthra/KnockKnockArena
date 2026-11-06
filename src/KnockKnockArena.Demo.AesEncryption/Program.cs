// M11 demo: symmetric encryption (AES) on a KnockKnock chat line — and its catch.
//
//   dotnet run --project src/KnockKnockArena.Demo.AesEncryption
//
// alice encrypts a chat message with AES-256; bob, who has the same key, reads it;
// mallory, who sniffed the packet (Wireshark), sees only ciphertext; carol, with a
// different key, cannot read it either. Then the problem nobody can dodge: alice and
// bob needed the SAME secret key before they could talk securely. Sending the key in
// the clear over the network gives it to mallory too. That is the key distribution
// problem, and the reason TLS starts with asymmetric cryptography (see
// Demo.RsaSigning) before switching to a symmetric cipher like this one.
using System.Security.Cryptography;
using System.Text;

string chat = "alice: meet at the rocket launcher, north side";
Console.WriteLine($"Plaintext chat ({Encoding.UTF8.GetByteCount(chat)} B): \"{chat}\"");
Console.WriteLine();

// A 256-bit key shared by alice and bob. How bob got it is the question at the end.
byte[] sharedKey = RandomNumberGenerator.GetBytes(32);
Console.WriteLine($"Shared key (alice + bob): {Convert.ToHexString(sharedKey)}");

// alice encrypts. A fresh random IV per message: the same text twice must not give
// the same ciphertext, or an eavesdropper learns when players repeat themselves.
(byte[] iv, byte[] cipher) = Encrypt(chat, sharedKey);
Console.WriteLine($"IV (sent in the clear):   {Convert.ToHexString(iv)}");
Console.WriteLine($"Ciphertext ({cipher.Length} B):      {Convert.ToHexString(cipher)}");
(byte[] iv2, byte[] cipher2) = Encrypt(chat, sharedKey);
Console.WriteLine($"Same text again:          {Convert.ToHexString(cipher2)[..32]}...  (different IV {Convert.ToHexString(iv2)[..8]}... -> different bytes)");
Console.WriteLine();

// What travels on the wire: IV + ciphertext. mallory captures exactly this.
byte[] packet = iv.Concat(cipher).ToArray();
Console.WriteLine($"mallory (Wireshark) sees: {Convert.ToHexString(packet)[..48]}...  as text: \"{Printable(packet)[..24]}...\"");

// bob decrypts with the shared key.
Console.WriteLine($"bob (same key) reads:     \"{Decrypt(packet, sharedKey)}\"");

// carol has a key too — just not this one.
byte[] carolKey = RandomNumberGenerator.GetBytes(32);
try
{
    Console.WriteLine($"carol (other key) reads:  \"{Decrypt(packet, carolKey)}\"");
}
catch (CryptographicException ex)
{
    Console.WriteLine($"carol (other key):        cannot decrypt ({ex.GetType().Name}: padding is invalid)");
}
Console.WriteLine();

// The key distribution problem, shown instead of told.
Console.WriteLine("How did bob get the key? If alice sends it over the network first:");
Console.WriteLine($"  alice -> bob: \"KEY {Convert.ToHexString(sharedKey)}\"");
Console.WriteLine($"  mallory captured that too, and now reads: \"{Decrypt(packet, sharedKey)}\"");
Console.WriteLine("AES keeps a secret only between players who ALREADY share one. Agreeing on that");
Console.WriteLine("secret over an open network takes asymmetric cryptography — what TLS does first.");

static (byte[] Iv, byte[] Cipher) Encrypt(string text, byte[] key)
{
    using Aes aes = Aes.Create();
    aes.Key = key;
    aes.GenerateIV();
    byte[] cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(text), aes.IV); // CBC + PKCS7 padding
    return (aes.IV, cipher);
}

static string Decrypt(byte[] packet, byte[] key)
{
    using Aes aes = Aes.Create();
    aes.Key = key;
    byte[] iv = packet[..16];
    byte[] plain = aes.DecryptCbc(packet[16..], iv);
    return Encoding.UTF8.GetString(plain);
}

static string Printable(byte[] bytes) => new(bytes.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
