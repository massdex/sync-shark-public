#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

// Framework-compatible counterpart of the release verifier. Windows SIP binds the
// file digest. Windows CryptVerifyTimeStampSignature binds the RFC3161 token to the
// primary signature; a separate chain is evaluated at that token's time. No custom
// ASN.1 decoder, timestamp service, trust-store write or private key is involved.
internal static class SyncSharkUpdaterSignature
{
    private const string TimestampOid="1.3.6.1.4.1.311.3.3.1",TimestampEku="1.3.6.1.5.5.7.3.8";

    [StructLayout(LayoutKind.Sequential)] private struct Blob {internal uint Size;internal IntPtr Data;}
    [StructLayout(LayoutKind.Sequential)] private struct Algorithm {internal IntPtr Oid;internal Blob Parameters;}
    [StructLayout(LayoutKind.Sequential)] private struct Attributes {internal uint Count;internal IntPtr Values;}
    [StructLayout(LayoutKind.Sequential)] private struct SignerInfoNative
    {internal uint Version;internal Blob Issuer,Serial;internal Algorithm Hash,Encryption;internal Blob EncryptedHash;internal Attributes Authenticated,Unauthenticated;}
    [StructLayout(LayoutKind.Sequential)] private struct TimestampContext {internal uint Size;internal IntPtr Encoded,Info;}
    [StructLayout(LayoutKind.Sequential)] private struct TimestampInfo
    {internal uint Version;internal IntPtr Policy;internal Algorithm Hash;internal Blob Message,Serial;internal System.Runtime.InteropServices.ComTypes.FILETIME Time;}
    [DllImport("crypt32.dll",SetLastError=true)] private static extern IntPtr CryptMsgOpenToDecode(uint encoding,uint flags,uint type,IntPtr provider,IntPtr recipient,IntPtr stream);
    [DllImport("crypt32.dll",SetLastError=true)] private static extern bool CryptMsgUpdate(IntPtr message,byte[] data,uint size,bool final);
    [DllImport("crypt32.dll",SetLastError=true)] private static extern bool CryptMsgGetParam(IntPtr message,uint parameter,uint index,IntPtr output,ref uint size);
    [DllImport("crypt32.dll")] private static extern bool CryptMsgClose(IntPtr message);
    [DllImport("crypt32.dll",SetLastError=true)] private static extern bool CryptVerifyTimeStampSignature(byte[] token,uint tokenSize,byte[] data,uint dataSize,
        IntPtr additionalStore,out IntPtr context,out IntPtr signer,out IntPtr store);
    [DllImport("crypt32.dll")] private static extern void CryptMemFree(IntPtr pointer);
    [DllImport("crypt32.dll")] private static extern bool CertFreeCertificateContext(IntPtr pointer);
    [DllImport("crypt32.dll")] private static extern bool CertCloseStore(IntPtr store,uint flags);

    internal static void Verify(string path,bool container)
    {
        // Hold() excludes links, offline placeholders and concurrent writes/deletes.
        using(SyncSharkDiscoverySetupNative.Hold(path,false))
        {
            SyncSharkDiscoverySetupNative.RequireProductSignature(path);
            byte[] bytes=ReadAuthenticode(path,container);
            var cms=new SignedCms();cms.Decode(bytes);cms.CheckSignature(true);
            Need(cms.SignerInfos.Count==1,"Exactly one primary release signer is required.");
            var primary=cms.SignerInfos[0];var certificate=primary.Certificate;
            Need(certificate!=null && (container?SyncSharkSignerIdentity.PackageThumbprints:SyncSharkSignerIdentity.PublisherThumbprints).Contains(certificate.Thumbprint) &&
                certificate.Subject==(container?SyncSharkSignerIdentity.PackageSubject:SyncSharkSignerIdentity.PublisherSubject) &&
                certificate.Issuer==(container?SyncSharkSignerIdentity.PackageSubject:SyncSharkSignerIdentity.PublisherSubject),"Release signer identity changed.");
            Need((container?SyncSharkSignerIdentity.PackageSpki:SyncSharkSignerIdentity.PublisherSpki).Contains(SpkiHash(certificate)),"Release public-key identity changed.");
            using(var chain=new X509Chain())
            {
                chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags=X509VerificationFlags.NoFlag;
                bool valid=chain.Build(certificate);
                Need(valid && chain.ChainStatus.Length==0 || !valid && chain.ChainStatus.Length==1 &&
                    chain.ChainStatus[0].Status==X509ChainStatusFlags.UntrustedRoot,"The signer differs by more than its isolated pinned untrusted root.");
            }
            CryptographicAttributeObject timestamp=null;
            foreach(CryptographicAttributeObject attribute in primary.UnsignedAttributes)
                if(attribute.Oid.Value==TimestampOid){Need(timestamp==null,"Duplicate RFC3161 tokens.");timestamp=attribute;}
            Need(timestamp!=null && timestamp.Values.Count==1,"A single RFC3161 timestamp is required.");
            byte[] token=timestamp.Values[0].RawData;
            Need(token.Length>0 && token.Length<=1048576,"Timestamp token is oversized.");
            VerifyTimestamp(token,SignatureBytes(bytes));
        }
    }
    private static void Need(bool condition,string message) {if(!condition)throw new CryptographicException(message);}
    private static byte[] SignatureBytes(byte[] cms)
    {
        IntPtr message=CryptMsgOpenToDecode(0x10001,0,0,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero),buffer=IntPtr.Zero;
        Need(message!=IntPtr.Zero,"Cannot inspect the primary signature.");
        try
        {
            Need(CryptMsgUpdate(message,cms,(uint)cms.Length,true),"Malformed primary CMS.");
            uint length=0;
            Need(CryptMsgGetParam(message,6,0,IntPtr.Zero,ref length) && length>=Marshal.SizeOf(typeof(SignerInfoNative)) && length<=1048576,"Invalid primary signer record.");
            buffer=Marshal.AllocHGlobal((int)length);
            Need(CryptMsgGetParam(message,6,0,buffer,ref length),"Cannot read primary signer record.");
            var signer=(SignerInfoNative)Marshal.PtrToStructure(buffer,typeof(SignerInfoNative));
            long offset=signer.EncryptedHash.Data.ToInt64()-buffer.ToInt64();
            Need(signer.EncryptedHash.Size>0 && signer.EncryptedHash.Size<=16384 && offset>=0 && offset+signer.EncryptedHash.Size<=length,"Invalid signature byte range.");
            var result=new byte[(int)signer.EncryptedHash.Size];Marshal.Copy(signer.EncryptedHash.Data,result,0,result.Length);return result;
        }
        finally {if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);CryptMsgClose(message);}
    }
    private static void VerifyTimestamp(byte[] token,byte[] signature)
    {
        IntPtr context=IntPtr.Zero,signer=IntPtr.Zero,store=IntPtr.Zero;
        try
        {
            Need(CryptVerifyTimeStampSignature(token,(uint)token.Length,signature,(uint)signature.Length,IntPtr.Zero,out context,out signer,out store) &&
                context!=IntPtr.Zero && signer!=IntPtr.Zero,"RFC3161 timestamp is not cryptographically bound to this signature.");
            var native=(TimestampContext)Marshal.PtrToStructure(context,typeof(TimestampContext));
            Need(native.Info!=IntPtr.Zero,"Missing RFC3161 timestamp information.");
            var info=(TimestampInfo)Marshal.PtrToStructure(native.Info,typeof(TimestampInfo));
            Need(info.Version==1 && info.Serial.Size>0,"Invalid RFC3161 timestamp version/serial.");
            DateTime time=DateTime.FromFileTimeUtc(((long)(uint)info.Time.dwHighDateTime<<32)|(uint)info.Time.dwLowDateTime);
            Need(time<=DateTime.UtcNow.AddMinutes(5),"Future timestamp refused.");
            var cms=new SignedCms();cms.Decode(token);cms.CheckSignature(true);
            Need(cms.ContentInfo.ContentType.Value=="1.2.840.113549.1.9.16.1.4" && cms.SignerInfos.Count==1,"Invalid timestamp content/signers.");
            using(var cert=new X509Certificate2(signer))
            using(var chain=new X509Chain())
            {
                Need(cms.SignerInfos[0].Certificate!=null && cms.SignerInfos[0].Certificate.Thumbprint==cert.Thumbprint,"Timestamp signer correlation failed.");
                var usages=cert.Extensions.Cast<X509Extension>().Where(e=>e.Oid.Value=="2.5.29.37").ToArray();
                Need(usages.Length==1 && usages[0].Critical,"Timestamp EKU must be exclusive and critical.");
                var eku=new X509EnhancedKeyUsageExtension(usages[0],true);
                Need(eku.EnhancedKeyUsages.Count==1 && eku.EnhancedKeyUsages[0].Value==TimestampEku,"Timestamp EKU changed.");
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid(TimestampEku));
                chain.ChainPolicy.VerificationTime=time;chain.ChainPolicy.VerificationFlags=X509VerificationFlags.NoFlag;
                chain.ChainPolicy.RevocationMode=X509RevocationMode.Online;chain.ChainPolicy.RevocationFlag=X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.UrlRetrievalTimeout=TimeSpan.FromSeconds(15);
                chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
                Need(chain.Build(cert) && chain.ChainStatus.Length==0,"Timestamp chain is invalid at the authenticated timestamp time.");
            }
        }
        finally {if(context!=IntPtr.Zero)CryptMemFree(context);if(signer!=IntPtr.Zero)CertFreeCertificateContext(signer);if(store!=IntPtr.Zero)CertCloseStore(store,0);}
    }
    private static string SpkiHash(X509Certificate2 certificate)
    {
        // This registered identity is RSA/rsaEncryption with NULL parameters. Build
        // its standard SPKI from the existing public key; do not generalise key types.
        Need(certificate.PublicKey.Oid.Value=="1.2.840.113549.1.1.1" && certificate.PublicKey.EncodedParameters.RawData.SequenceEqual(new byte[]{5,0}),"Unexpected product public-key algorithm.");
        byte[] algorithm={0x30,0x0d,0x06,0x09,0x2a,0x86,0x48,0x86,0xf7,0x0d,0x01,0x01,0x01,0x05,0x00};
        byte[] key=Wrap(3,new byte[]{0}.Concat(certificate.PublicKey.EncodedKeyValue.RawData).ToArray());
        using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Wrap(0x30,algorithm.Concat(key).ToArray()))).Replace("-","").ToLowerInvariant();
    }
    private static byte[] Wrap(byte tag,byte[] value)
    {
        using(var memory=new MemoryStream())
        {
            memory.WriteByte(tag);
            if(value.Length<128)memory.WriteByte((byte)value.Length);
            else if(value.Length<=255){memory.WriteByte(0x81);memory.WriteByte((byte)value.Length);}
            else {Need(value.Length<=65535,"Oversized public key.");memory.WriteByte(0x82);memory.WriteByte((byte)(value.Length>>8));memory.WriteByte((byte)value.Length);}
            memory.Write(value,0,value.Length);return memory.ToArray();
        }
    }
    private static byte[] ReadAuthenticode(string path,bool container)
    {
        long size=new FileInfo(path).Length;Need(size>0 && size<=67108864,"Signed member exceeds the bounded signature inspection size.");
        if(container)
        {
            Need(Path.GetExtension(path)==".msix" && size<=4194304,"Unexpected identity-container input.");
            using(var stream=File.OpenRead(path))
            using(var archive=new ZipArchive(stream,ZipArchiveMode.Read))
            {
                var entries=archive.Entries.Where(e=>String.Equals(e.FullName,"AppxSignature.p7x",StringComparison.OrdinalIgnoreCase)).ToArray();
                Need(entries.Length==1 && entries[0].FullName=="AppxSignature.p7x" && entries[0].Length>=5 && entries[0].Length<=1048576,"Invalid container signature member.");
                using(var input=entries[0].Open())
                {
                    var value=new byte[(int)entries[0].Length];int offset=0;
                    while(offset<value.Length){int n=input.Read(value,offset,value.Length-offset);Need(n>0,"Truncated container signature.");offset+=n;}
                    Need(input.ReadByte()==-1 && value[0]=='P' && value[1]=='K' && value[2]=='C' && value[3]=='X',"Invalid container signature header.");
                    return value.Skip(4).ToArray();
                }
            }
        }
        if(Path.GetExtension(path)==".ps1")
        {
            Need(size<=1048576,"Oversized signed installer.");string text=File.ReadAllText(path,new UTF8Encoding(false,true));
            const string begin="# SIG # Begin signature block",end="# SIG # End signature block";
            int first=text.IndexOf(begin,StringComparison.Ordinal),last=text.IndexOf(end,StringComparison.Ordinal);
            Need(first>=0 && last>first && first==text.LastIndexOf(begin,StringComparison.Ordinal) && last==text.LastIndexOf(end,StringComparison.Ordinal) &&
                text.Substring(last+end.Length).Trim().Length==0,"Invalid installer signature block.");
            var encoded=new StringBuilder();
            foreach(string line in text.Substring(first+begin.Length,last-first-begin.Length).Split('\n'))
            {string content=line.Trim();if(content.Length==0)continue;Need(content.StartsWith("# ",StringComparison.Ordinal),"Invalid signature encoding.");encoded.Append(content.Substring(2));}
            return Convert.FromBase64String(encoded.ToString());
        }
        byte[] pe=File.ReadAllBytes(path);Need(pe.Length>=256 && BitConverter.ToUInt16(pe,0)==0x5a4d,"Invalid PE signature input.");
        int header=BitConverter.ToInt32(pe,0x3c);Need(header>=0 && header<=pe.Length-256 && BitConverter.ToUInt32(pe,header)==0x4550,"Invalid PE header.");
        int optional=header+24;ushort magic=BitConverter.ToUInt16(pe,optional);Need(magic==0x20b || magic==0x10b,"Invalid PE format.");
        int directory=optional+(magic==0x20b?112:96)+32;
        uint start=BitConverter.ToUInt32(pe,directory),length=BitConverter.ToUInt32(pe,directory+4);
        Need(start>0 && length>=8 && (ulong)start+length<=(ulong)pe.Length,"Invalid PE certificate range.");
        uint certificateLength=BitConverter.ToUInt32(pe,(int)start);
        Need(certificateLength>=8 && certificateLength<=1048576 && certificateLength<=length && ((certificateLength+7u)&~7u)==length &&
            BitConverter.ToUInt16(pe,(int)start+4)==0x200 && BitConverter.ToUInt16(pe,(int)start+6)==2,"Invalid or multiple PE certificate records.");
        byte[] cms=new byte[(int)certificateLength-8];Buffer.BlockCopy(pe,(int)start+8,cms,0,cms.Length);return cms;
    }
}
