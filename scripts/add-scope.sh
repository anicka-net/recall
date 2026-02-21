#!/bin/bash
# Add a new scoped user to Recall
# Usage: ./add-scope.sh <scope-name>
#
# Generates a random passphrase, computes its SHA256 hash,
# and prints the config entry + passphrase to give to the user.

set -euo pipefail

if [ $# -lt 1 ]; then
    echo "Usage: $0 <scope-name>"
    echo "Example: $0 work-laptop"
    exit 1
fi

SCOPE_NAME="$1"
PASSPHRASE=$(openssl rand -base64 24)
HASH=$(echo -n "$PASSPHRASE" | sha256sum | cut -d' ' -f1)

echo ""
echo "=== New Recall Scope ==="
echo ""
echo "Scope name:  $SCOPE_NAME"
echo "Passphrase:  $PASSPHRASE"
echo "SHA256 hash: $HASH"
echo ""
echo "Add to ~/.recall/config.json on the server:"
echo ""
echo "  \"Scopes\": ["
echo "    { \"Name\": \"$SCOPE_NAME\", \"SecretHash\": \"$HASH\" }"
echo "  ]"
echo ""
echo "Give the passphrase to the user (securely!)."
echo "They store it in ~/.recall-secret on their machine."
echo "Then follow ONBOARDING.md to set up hooks."
echo ""
echo "After updating config.json, restart the service:"
echo "  systemctl --user restart recall.service"
