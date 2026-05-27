using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Kagura.Data;

public class EncryptedStringConverter : ValueConverter<string, string>
{
    public EncryptedStringConverter(IDataProtector protector)
        : base(
            plaintext => string.IsNullOrEmpty(plaintext) ? plaintext : protector.Protect(plaintext),
            ciphertext => string.IsNullOrEmpty(ciphertext) ? ciphertext : protector.Unprotect(ciphertext))
    { }
}
