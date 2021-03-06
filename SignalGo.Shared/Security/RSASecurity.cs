﻿#if (!PORTABLE)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SignalGo.Shared.Security
{
    public class RSAKey
    {
        public string PublicKey { get; set; }
        public string PrivateKey { get; set; }
    }

    public static class RSASecurity
    {
        //public static RSAKey GenerateRandomKey()
        //{
        //    using (var csp = new RSACryptoServiceProvider(2048))
        //    {
        //        var privKey = csp.ExportParameters(true);

        //        var pubKey = csp.ExportParameters(false);

        //        string pubKeyString, privKeyString;

        //        using (var sw = new System.IO.StringWriter())
        //        {
        //            var xs = new System.Xml.Serialization.XmlSerializer(typeof(RSAParameters));
        //            xs.Serialize(sw, pubKey);
        //            pubKeyString = sw.ToString();
        //        }
        //        using (var sw = new System.IO.StringWriter())
        //        {
        //            var xs = new System.Xml.Serialization.XmlSerializer(typeof(RSAParameters));
        //            xs.Serialize(sw, privKey);
        //            privKeyString = sw.ToString();
        //        }
        //        return new RSAKey() { PrivateKey = privKeyString, PublicKey = pubKeyString };
        //    }
        //}

        //public static RSAParameters StringToKey(string xml)
        //{
        //    using (var sr = new System.IO.StringReader(xml))
        //    {
        //        var xs = new System.Xml.Serialization.XmlSerializer(typeof(RSAParameters));
        //        return (RSAParameters)xs.Deserialize(sr);
        //    }
        //}

        //public static byte[] Encrypt(byte[] bytes, RSAParameters publicKey)
        //{
        //    using (var csp = new RSACryptoServiceProvider())
        //    {
        //        csp.ImportParameters(publicKey);
        //        var bytesCypherText = csp.Encrypt(bytes, false);
        //        return bytesCypherText;
        //    }
        //}

        //public static byte[] Decrypt(byte[] bytes, RSAParameters privateKey)
        //{
        //    using (var csp = new RSACryptoServiceProvider())
        //    {
        //        csp.ImportParameters(privateKey);
        //        var bytesCypherText = csp.Decrypt(bytes, false);
        //        return bytesCypherText;
        //    }
        //}
        public static RSAKey GenerateRandomKey()
        {
            using (var csp = RSA.Create())
            {
                csp.KeySize = 2048;
                var privKey = csp.ExportParameters(true);

                var pubKey = csp.ExportParameters(false);

                string pubKeyString, privKeyString;

                using (var sw = new System.IO.StringWriter())
                {
                    var xs = new System.Xml.Serialization.XmlSerializer(typeof(RSAParameters));
                    xs.Serialize(sw, pubKey);
                    pubKeyString = sw.ToString();
                }
                using (var sw = new System.IO.StringWriter())
                {
                    var xs = new System.Xml.Serialization.XmlSerializer(typeof(RSAParameters));
                    xs.Serialize(sw, privKey);
                    privKeyString = sw.ToString();
                }
                return new RSAKey() { PrivateKey = privKeyString, PublicKey = pubKeyString };
            }
        }

        public static RSAParameters StringToKey(string xml)
        {
            using (var sr = new System.IO.StringReader(xml))
            {
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(RSAParameters));
                return (RSAParameters)xs.Deserialize(sr);
            }
        }

        public static byte[] Encrypt(byte[] bytes, RSAParameters publicKey)
        {
            using (var csp = RSA.Create())
            {
                
                csp.ImportParameters(publicKey);
#if (NETSTANDARD1_6 ||NETCOREAPP1_1)
                var bytesCypherText = csp.Encrypt(bytes, RSAEncryptionPadding.OaepSHA1);
#else
                var bytesCypherText = csp.EncryptValue(bytes);
#endif
                return bytesCypherText;
            }
        }

        public static byte[] Decrypt(byte[] bytes, RSAParameters privateKey)
        {
            using (var csp = RSA.Create())
            {
                csp.ImportParameters(privateKey);
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                var bytesCypherText = csp.Decrypt(bytes, RSAEncryptionPadding.OaepSHA1);
#else
                               var bytesCypherText = csp.DecryptValue(bytes);
#endif
                return bytesCypherText;
            }
        }
    }
}
#endif
