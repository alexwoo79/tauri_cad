using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SunlightPlugin
{
    internal sealed class LicenseStatus
    {
        public bool IsValid;
        public bool IsDebugBypass;
        public string Message;
        public string MachineCode;
        public string CustomerName;
        public string ProductCode;
        public DateTime? ExpiryDate;
        public string LicensePath;
    }

    internal static class LicenseManager
    {
        private const string ProductCode = "SunlightPlugin";
        private const string LicenseVersion = "v1";
        private const string PublicKeyXml = "<RSAKeyValue><Modulus>REPLACE_WITH_YOUR_RSA_PUBLIC_KEY_MODULUS</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        private const string PlaceholderToken = "REPLACE_WITH_YOUR_RSA_PUBLIC_KEY_MODULUS";

        private static readonly string LicenseDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SunlightPlugin",
            "License");

        private static readonly string LicenseFilePathValue = Path.Combine(LicenseDirectoryPath, "license.dat");

        public static string GetMachineCode()
        {
            string hex = ComputeFingerprintHex();
            if (hex.Length < 32) hex = hex.PadRight(32, '0');
            hex = hex.Substring(0, 32).ToUpperInvariant();
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1}-{2}-{3}",
                hex.Substring(0, 8),
                hex.Substring(8, 8),
                hex.Substring(16, 8),
                hex.Substring(24, 8));
        }

        public static LicenseStatus GetCurrentStatus()
        {
            var status = CreateBaseStatus();

            if (!IsPublicKeyConfigured())
            {
#if DEBUG
                status.IsValid = true;
                status.IsDebugBypass = true;
                status.Message = "DEBUG 构建下未配置公钥，已启用调试旁路。Release 发布前请替换 LicenseManager.PublicKeyXml。";
                return status;
#else
                status.Message = "未配置授权公钥。请先在 LicenseManager.PublicKeyXml 中替换为你的 RSA 公钥。";
                return status;
#endif
            }

            if (!File.Exists(LicenseFilePathValue))
            {
                status.Message = "未找到授权文件。请先运行 SUNLICINFO 获取机器码，再运行 SUNLICACT 导入授权码。";
                return status;
            }

            try
            {
                string activationCode = File.ReadAllText(LicenseFilePathValue, Encoding.UTF8).Trim();
                return ValidateActivationCode(activationCode, persistOnSuccess: false);
            }
            catch (Exception ex)
            {
                status.Message = "读取授权文件失败: " + ex.Message;
                return status;
            }
        }

        public static LicenseStatus Activate(string activationCode)
        {
            return ValidateActivationCode(activationCode, persistOnSuccess: true);
        }

        public static void ClearLicense()
        {
            try
            {
                if (File.Exists(LicenseFilePathValue))
                {
                    File.Delete(LicenseFilePathValue);
                }
            }
            catch
            {
            }
        }

        public static string BuildStatusSummary(LicenseStatus status)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SunlightPlugin 授权状态");
            sb.AppendLine("产品: " + (status.ProductCode ?? ProductCode));
            sb.AppendLine("机器码: " + (status.MachineCode ?? GetMachineCode()));
            sb.AppendLine("授权文件: " + status.LicensePath);

            if (!string.IsNullOrWhiteSpace(status.CustomerName))
            {
                sb.AppendLine("授权对象: " + status.CustomerName);
            }
            if (status.ExpiryDate.HasValue)
            {
                sb.AppendLine("到期日: " + status.ExpiryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            sb.AppendLine("状态: " + (status.IsValid ? "有效" : "无效"));
            if (status.IsDebugBypass)
            {
                sb.AppendLine("说明: 当前为 DEBUG 调试旁路，仅供内部开发。Release 不会旁路。");
            }
            if (!string.IsNullOrWhiteSpace(status.Message))
            {
                sb.AppendLine("详情: " + status.Message);
            }
            return sb.ToString().TrimEnd();
        }

        private static LicenseStatus ValidateActivationCode(string activationCode, bool persistOnSuccess)
        {
            var status = CreateBaseStatus();

            if (!IsPublicKeyConfigured())
            {
#if DEBUG
                status.IsValid = true;
                status.IsDebugBypass = true;
                status.Message = "DEBUG 构建下未配置公钥，跳过授权校验。";
                return status;
#else
                status.Message = "未配置授权公钥，无法校验授权码。";
                return status;
#endif
            }

            if (string.IsNullOrWhiteSpace(activationCode))
            {
                status.Message = "授权码为空。";
                return status;
            }

            if (!TrySplitActivationCode(activationCode.Trim(), out string payloadBase64, out byte[] payloadBytes, out byte[] signatureBytes, out string parseError))
            {
                status.Message = parseError;
                return status;
            }

            if (!VerifySignature(payloadBytes, signatureBytes))
            {
                status.Message = "授权码签名校验失败。";
                return status;
            }

            string payloadText = Encoding.UTF8.GetString(payloadBytes);
            Dictionary<string, string> values = ParsePayload(payloadText);

            if (!values.TryGetValue("version", out string version) || !string.Equals(version, LicenseVersion, StringComparison.OrdinalIgnoreCase))
            {
                status.Message = "授权码版本不匹配。";
                return status;
            }

            if (!values.TryGetValue("product", out string product) || !string.Equals(product, ProductCode, StringComparison.Ordinal))
            {
                status.Message = "授权码产品标识不匹配。";
                return status;
            }

            if (!values.TryGetValue("machine", out string machineCode) || !string.Equals(NormalizeMachineCode(machineCode), NormalizeMachineCode(status.MachineCode), StringComparison.OrdinalIgnoreCase))
            {
                status.Message = "授权码机器码不匹配。";
                return status;
            }

            if (values.TryGetValue("expiry", out string expiryText) && !string.IsNullOrWhiteSpace(expiryText))
            {
                if (!DateTime.TryParseExact(expiryText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime expiryDate))
                {
                    status.Message = "授权码到期日格式无效。";
                    return status;
                }

                status.ExpiryDate = expiryDate;
                if (DateTime.Today > expiryDate.Date)
                {
                    status.Message = "授权已过期。";
                    return status;
                }
            }

            if (values.TryGetValue("customer", out string customerName))
            {
                status.CustomerName = customerName;
            }

            status.ProductCode = product;
            status.IsValid = true;
            status.Message = persistOnSuccess ? "授权已写入本机。" : "授权有效。";

            if (persistOnSuccess)
            {
                Directory.CreateDirectory(LicenseDirectoryPath);
                File.WriteAllText(LicenseFilePathValue, payloadBase64 + "." + Convert.ToBase64String(signatureBytes), Encoding.UTF8);
            }

            return status;
        }

        private static LicenseStatus CreateBaseStatus()
        {
            return new LicenseStatus
            {
                IsValid = false,
                ProductCode = ProductCode,
                MachineCode = GetMachineCode(),
                LicensePath = LicenseFilePathValue,
                Message = string.Empty
            };
        }

        private static bool TrySplitActivationCode(string activationCode, out string payloadBase64, out byte[] payloadBytes, out byte[] signatureBytes, out string error)
        {
            payloadBase64 = null;
            payloadBytes = null;
            signatureBytes = null;
            error = null;

            int separator = activationCode.IndexOf('.');
            if (separator <= 0 || separator >= activationCode.Length - 1)
            {
                error = "授权码格式无效，应为 payload.signature。";
                return false;
            }

            payloadBase64 = activationCode.Substring(0, separator).Trim();
            string signatureBase64 = activationCode.Substring(separator + 1).Trim();

            try
            {
                payloadBytes = Convert.FromBase64String(payloadBase64);
                signatureBytes = Convert.FromBase64String(signatureBase64);
                return true;
            }
            catch (FormatException)
            {
                error = "授权码 Base64 内容无效。";
                return false;
            }
        }

        private static Dictionary<string, string> ParsePayload(string payloadText)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] parts = payloadText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                int idx = parts[i].IndexOf('=');
                if (idx <= 0) continue;
                string key = parts[i].Substring(0, idx).Trim();
                string value = parts[i].Substring(idx + 1).Trim();
                values[key] = value;
            }
            return values;
        }

        private static bool VerifySignature(byte[] payloadBytes, byte[] signatureBytes)
        {
            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.FromXmlString(PublicKeyXml);
                    return rsa.VerifyData(payloadBytes, CryptoConfig.MapNameToOID("SHA256"), signatureBytes);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPublicKeyConfigured()
        {
            return !string.IsNullOrWhiteSpace(PublicKeyXml)
                && PublicKeyXml.IndexOf(PlaceholderToken, StringComparison.Ordinal) < 0;
        }

        private static string ComputeFingerprintHex()
        {
            string raw = string.Join("|", new[]
            {
                ReadMachineGuid(),
                Environment.MachineName ?? string.Empty,
                Environment.UserDomainName ?? string.Empty,
                ProductCode
            });

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("X2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        private static string ReadMachineGuid()
        {
            string value = TryReadMachineGuid(RegistryView.Registry64);
            if (!string.IsNullOrWhiteSpace(value)) return value;

            value = TryReadMachineGuid(RegistryView.Registry32);
            if (!string.IsNullOrWhiteSpace(value)) return value;

            return "UNKNOWN-MACHINE-GUID";
        }

        private static string TryReadMachineGuid(RegistryView view)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey subKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    return subKey?.GetValue("MachineGuid") as string;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeMachineCode(string machineCode)
        {
            return (machineCode ?? string.Empty).Replace("-", string.Empty).Trim().ToUpperInvariant();
        }
    }
}