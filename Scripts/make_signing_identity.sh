#!/usr/bin/env bash
#
# Creates Slovo's own code-signing identity, "Slovo Local", in the login keychain.
#
# macOS ties Microphone, Accessibility, Input Monitoring and Screen & System Audio
# Recording grants to the app's signature. An ad-hoc signature changes with every
# build, so every reinstall would lose them. A self-signed certificate gives Slovo one
# identity for good: grants given once survive every rebuild and reinstall on this Mac.
# The certificate stays on this Mac and cannot sign apps for other people (that needs
# a Developer ID and notarization).
#
# Run it once:  ./Scripts/make_signing_identity.sh
# macOS then asks to confirm a change to certificate trust settings: that is you
# trusting your own new certificate. After that, build.py signs with it on its own.
#
set -euo pipefail

IDENTITY="Slovo Local"
KEYCHAIN="${HOME}/Library/Keychains/login.keychain-db"

if security find-identity -v -p codesigning 2>/dev/null | grep -q "\"${IDENTITY}\""; then
    echo "The identity '${IDENTITY}' already exists; nothing to do."
    exit 0
fi

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT
chmod 700 "${WORK}"

if security find-certificate -c "${IDENTITY}" "${KEYCHAIN}" >/dev/null 2>&1; then
    # A run whose trust dialog was cancelled already imported the pair. Importing a
    # second one would leave two identities with the same name, which codesign refuses.
    echo "The certificate is already in the keychain; only trusting it."
    security find-certificate -c "${IDENTITY}" -p "${KEYCHAIN}" > "${WORK}/cert.pem"
else
    # codesign refuses a certificate without the codeSigning extended key usage.
    cat > "${WORK}/cert.cnf" <<EOF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = ${IDENTITY}
[ext]
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
basicConstraints = critical,CA:FALSE
EOF

    echo "Generating the key and a certificate valid for 20 years…"
    openssl req -x509 -newkey rsa:2048 -days 7300 -nodes \
        -keyout "${WORK}/key.pem" -out "${WORK}/cert.pem" \
        -config "${WORK}/cert.cnf" 2>/dev/null

    # A one-off password only carries the pair through PKCS#12. The legacy algorithms
    # are forced because `security import` cannot read OpenSSL 3's default AES/PBKDF2
    # container and reports a misleading "MAC verification failed".
    P12PASS="$(openssl rand -hex 16)"
    openssl pkcs12 -export -out "${WORK}/identity.p12" \
        -inkey "${WORK}/key.pem" -in "${WORK}/cert.pem" \
        -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1 \
        -passout "pass:${P12PASS}" -name "${IDENTITY}"

    echo "Importing it into the login keychain…"
    # -T lets codesign use the key without asking on every build.
    security import "${WORK}/identity.p12" -k "${KEYCHAIN}" -P "${P12PASS}" -T /usr/bin/codesign
fi

echo "Trusting it for code signing: macOS asks you to confirm with your password."
security add-trusted-cert -p codeSign -k "${KEYCHAIN}" "${WORK}/cert.pem"

echo
if security find-identity -v -p codesigning | grep -q "\"${IDENTITY}\""; then
    echo "Done. python3 build.py now signs Slovo as '${IDENTITY}'."
    echo "Grant Slovo's permissions once more after installing that build; they stay from then on."
else
    echo "The identity is not valid yet. If you cancelled the dialog, run this script again."
    echo "Otherwise open Keychain Access → login → My Certificates → '${IDENTITY}' →"
    echo "Trust → Code Signing: Always Trust."
    exit 1
fi
