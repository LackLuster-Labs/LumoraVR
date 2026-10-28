// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Lumora.Nexus.Transport.LNQ;

// QUIC will not run without TLS, and a host has no name a public authority would sign. Each run makes its own
// short-lived certificate; nothing trusts it, and nothing has to: the crypto handshake inside the link and the
// signed join are what prove who is on the other end, exactly as over LNL. -xlinka
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class LNQCertificate
{
    private static readonly object Gate = new();
    private static X509Certificate2? _current;

    public static X509Certificate2 Current
    {
        get
        {
            lock (Gate)
            {
                if (_current != null && _current.NotAfter > DateTime.Now.AddDays(1))
                    return _current;
                _current = Make();
                return _current;
            }
        }
    }

    private static X509Certificate2 Make()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=lumora-session", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var now = DateTimeOffset.UtcNow;
        using var made = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
        // A key that only lives in memory is not usable by the Windows TLS stack; one loaded from a PFX is.
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }
}
