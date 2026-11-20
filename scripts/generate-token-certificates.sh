#!/usr/bin/env bash
# Generates the two PFX files the registry signs and encrypts its OAuth tokens with in production
# (docs/deploy/production.md). Run once per deployment and keep the output: replacing either file signs
# every user out.
#
#   scripts/generate-token-certificates.sh <output-dir>
#
# Writes <output-dir>/signing.pfx, encryption.pfx and passwords.env (the two random passwords, as the
# OpenIddict__*__Password variables). Needs openssl. Refuses to overwrite existing files.
set -euo pipefail

out="${1:?usage: $0 <output-dir>}"
mkdir -p "$out"
for f in signing.pfx encryption.pfx passwords.env; do
  if [[ -e "$out/$f" ]]; then
    echo "$out/$f already exists; not overwriting it (replacing a key signs every user out)." >&2
    exit 1
  fi
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
umask 077

# make <name> <key usage> <password>: a self-signed RSA 3072 certificate valid for ten years. OpenIddict
# only uses the key pair; nobody validates the chain, so self-signed is correct here. The key usage must
# match the role, or OpenIddict refuses the certificate.
make() {
  local name="$1" usage="$2" password="$3"
  openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes \
    -subj "/CN=protostar-registry token ${name}" \
    -addext "keyUsage=critical,${usage}" \
    -keyout "$work/$name.key" -out "$work/$name.crt" 2>/dev/null
  openssl pkcs12 -export -inkey "$work/$name.key" -in "$work/$name.crt" \
    -passout "pass:${password}" -out "$out/$name.pfx"
}

signing_password="$(openssl rand -hex 24)"
encryption_password="$(openssl rand -hex 24)"
make signing digitalSignature "$signing_password"
make encryption keyEncipherment "$encryption_password"

cat > "$out/passwords.env" <<ENV
OpenIddict__SigningCertificate__Password=${signing_password}
OpenIddict__EncryptionCertificate__Password=${encryption_password}
ENV

echo "Wrote $out/signing.pfx, $out/encryption.pfx and $out/passwords.env."
echo "The container runs as UID 1654: make the .pfx files readable by it, for example"
echo "  sudo chown 1654 $out/*.pfx   (they are mode 600 now)"
