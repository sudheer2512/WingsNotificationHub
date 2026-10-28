#!/usr/bin/env bash
# End-to-end demonstration of Wings Notification Hub against a running dev instance (default http://localhost:5480).
# Uses only the seeded demo data and mock providers — no paid messaging accounts.
#   scripts/demo.sh [base-url]
set -euo pipefail
BASE="${1:-http://localhost:5480}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CREDS="$ROOT/App_Data/dev-credentials.env"
[ -f "$CREDS" ] || { echo "Missing $CREDS — start the API in Development once to seed demo data."; exit 1; }
set -a; source "$CREDS"; set +a

json() { python3 -c "import sys,json; d=json.load(sys.stdin); print($1)"; }
step() { printf '\n\033[1;34m▶ %s\033[0m\n' "$*"; }
api() { local method=$1 path=$2; shift 2; curl -sS -X "$method" "$BASE$path" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' "$@"; }

step "1. Application authenticates with OAuth client credentials (Wings ERP Auto)"
TOKEN=$(curl -sS -X POST "$BASE/connect/token" -d grant_type=client_credentials -d "client_id=$ERP_AUTO_DEMO_CLIENT_ID" -d "client_secret=$ERP_AUTO_DEMO_CLIENT_SECRET" | json "d['access_token']")
echo "token acquired (${#TOKEN} chars)"

step "2. Send the specification's INVOICE_CREATED example on email + WhatsApp + in-app"
KEY="demo-invoice-$(date +%s)"
BODY=$(cat <<EOF
{"companyId":"$DEMO_COMPANY_ABC_ID","notificationType":"INVOICE_CREATED","templateCode":"INVOICE_CREATED_V1",
 "recipient":{"customerId":"CUST-1001","email":"customer@example.com","mobile":"+919876543210","userId":"u-1001"},
 "channels":["email","whatsapp","inapp"],
 "variables":{"CustomerName":"ABC Traders","InvoiceNumber":"INV-10045","InvoiceAmount":"25000","InvoiceDate":"2026-10-28"},
 "priority":"normal","idempotencyKey":"$KEY"}
EOF
)
RESP=$(api POST /api/v1/notifications/send -d "$BODY")
echo "$RESP" | python3 -m json.tool
NID=$(echo "$RESP" | json "d['notificationId']")

step "3. Retrying with the same idempotency key returns the same notification (no duplicate)"
api POST /api/v1/notifications/send -d "$BODY" | json "f\"notificationId={d['notificationId']} duplicate={d['duplicate']}\""

step "4. Delivery timeline (provider acceptance → delivered → read receipts)"
sleep 2
api GET "/api/v1/notifications/$NID/history" | python3 -c "
import sys,json; d=json.load(sys.stdin)
for x in d['deliveries']: print(f\"  {x['channel']:9} {x['status']:16} via {x.get('provider')}  attempts={x['attempts']}  cost={x.get('cost')}\")
print('  events:'); [print(f\"    {e['recordedAt'][11:23]} {e['type']:16} {str(e.get('status')):16} {e['source']}\") for e in d['events']]"

step "5. Business event → rule engine (payment.overdue matches the nested AND/OR rule → SMS + email)"
EV=$(api POST /api/v1/events -d "{\"companyId\":\"$DEMO_COMPANY_ABC_ID\",\"eventType\":\"payment.overdue\",\"payload\":{\"customer\":{\"customerId\":\"CUST-1001\",\"tier\":\"gold\"},\"CustomerName\":\"ABC Traders\",\"InvoiceNumber\":\"INV-10045\",\"AmountDue\":\"25000\",\"DueDate\":\"2026-10-10\",\"DaysOverdue\":18}}" | json "d['eventId']")
sleep 2
api GET "/api/v1/events/$EV" | python3 -m json.tool

step "6. Failure handling: SMS to a number the mock treats as flaky (…1111) fails over to the secondary provider"
FID=$(api POST /api/v1/notifications/send -d "{\"companyId\":\"$DEMO_COMPANY_ABC_ID\",\"notificationType\":\"INVOICE_CREATED\",\"recipient\":{\"mobile\":\"+919800001111\"},\"channels\":[\"sms\"],\"variables\":{\"CustomerName\":\"X\",\"InvoiceNumber\":\"I-1\",\"InvoiceAmount\":\"1\",\"InvoiceDate\":\"2026-10-28\"}}" | json "d['notificationId']")
sleep 12
api GET "/api/v1/notifications/$FID/history" | python3 -c "
import sys,json; d=json.load(sys.stdin)
[print(f\"  attempt {a['attemptNumber']}: {a.get('provider'):22} {a.get('outcome')}  {a.get('errorCode') or ''}\") for a in d['attempts']]
print('  status:', d['deliveries'][0]['status'], '(retries continue with backoff if still pending)')"

step "7. Consent: WhatsApp to a customer without opt-in is suppressed and falls back to SMS"
api POST /api/v1/notifications/send -d "{\"companyId\":\"$DEMO_COMPANY_ABC_ID\",\"notificationType\":\"INVOICE_CREATED\",\"recipient\":{\"customerId\":\"CUST-1002\"},\"channels\":[\"whatsapp\"],\"fallbacks\":[{\"from\":\"whatsapp\",\"to\":\"sms\"}],\"variables\":{\"CustomerName\":\"Gupta Stores\",\"InvoiceNumber\":\"I-2\",\"InvoiceAmount\":\"10\",\"InvoiceDate\":\"2026-10-28\"}}" \
  | json "'  '+'; '.join(f\"{x['channel']}={x['status']} ({x.get('reason','')})\" for x in d['deliveries'])"

step "8. In-app inbox: the ERP backend mints an inbox token for user u-1001, who reads their messages"
INBOX=$(api POST /api/v1/inapp/tokens -d "{\"companyId\":\"$DEMO_COMPANY_ABC_ID\",\"userId\":\"u-1001\"}" | json "d['accessToken']")
curl -sS "$BASE/api/v1/inapp/messages" -H "Authorization: Bearer $INBOX" | json "f\"  unread={d['unread']}; latest: {d['items'][0]['title'] if d['items'] else '-'}\""

step "9. Customer replies on WhatsApp → correlated to the invoice and routed to the ERP webhook"
ADMIN=$(curl -sS -X POST "$BASE/api/v1/auth/login" -H 'Content-Type: application/json' -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}" | json "d['accessToken']")
WA=$(api GET "/api/v1/notifications/$NID/status" | json "[x for x in d['deliveries'] if x['channel']=='whatsapp'][0]")
WA_ACC=$(echo "$WA" | python3 -c "import sys,ast; print(ast.literal_eval(sys.stdin.read())['providerAccountId'])")
WA_MSG=$(echo "$WA" | python3 -c "import sys,ast; print(ast.literal_eval(sys.stdin.read())['providerMessageId'])")
curl -sS -X POST "$BASE/api/v1/dev/simulate-inbound" -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' \
  -d "{\"providerAccountId\":\"$WA_ACC\",\"from\":\"+919876543210\",\"body\":\"Please send my account statement.\",\"inReplyToProviderMessageId\":\"$WA_MSG\"}" >/dev/null
sleep 2
curl -sS "$BASE/api/v1/conversations?channel=whatsapp" -H "Authorization: Bearer $ADMIN" | json "'  '+'; '.join(f\"{t['externalAddress']} unread={t['unreadCount']} window_open={t['windowOpen']} last='{t.get('lastMessage')}' routing={t.get('routing')}\" for t in d[:3])"

step "10. AI agent via MCP: allowed type is sent; payment reminders wait for human approval"
AI=$(curl -sS -X POST "$BASE/connect/token" -d grant_type=client_credentials -d "client_id=$AI_AGENT_DEMO_CLIENT_ID" -d "client_secret=$AI_AGENT_DEMO_CLIENT_SECRET" | json "d['access_token']")
curl -sS -X POST "$BASE/mcp" -H "Authorization: Bearer $AI" -H 'Content-Type: application/json' \
  -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"notification.send\",\"arguments\":{\"companyId\":\"$DEMO_COMPANY_ABC_ID\",\"notificationType\":\"PAYMENT_OVERDUE\",\"recipient\":{\"customerId\":\"CUST-1001\"},\"channels\":[\"email\"],\"variables\":{\"CustomerName\":\"ABC\",\"InvoiceNumber\":\"INV-1\",\"AmountDue\":\"100\",\"DueDate\":\"2026-10-01\",\"DaysOverdue\":\"27\"}}}}" \
  | json "'  status: ' + d['result']['structuredContent']['status']"

step "11. Dashboard"
curl -sS "$BASE/api/v1/dashboard" -H "Authorization: Bearer $ADMIN" | json "'  ' + ', '.join(f'{k}={d[k]}' for k in ['totalNotifications','totalDeliveries','delivered','failed','pending','successRate','estimatedCost'])"

printf '\nDone. Admin console: %s  (sign in as ADMIN_EMAIL from App_Data/dev-credentials.env) · API docs: %s/api-docs\n' "$BASE" "$BASE"
