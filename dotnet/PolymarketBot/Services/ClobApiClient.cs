using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nethereum.Signer;
using Nethereum.Util;

namespace PolymarketBot.Services;

/// <summary>
/// Polymarket CLOB API client with proper EIP-712 + HMAC authentication.
/// Implements the same auth protocol as py-clob-client.
/// </summary>
public sealed class ClobApiClient
{
    // EIP-712 auth constants
    private const string AuthDomainName = "ClobAuthDomain";
    private const string ExchangeDomainName = "Polymarket CTF Exchange";
    private const string DomainVersion = "1";
    private const string AuthMessage = "This message attests that I control the given wallet";

    // Precomputed type hashes
    private static readonly byte[] AuthDomainTypeHash;
    private static readonly byte[] ExchangeDomainTypeHash;
    private static readonly byte[] ClobAuthTypeHash;
    private static readonly byte[] OrderTypeHash;

    static ClobApiClient()
    {
        AuthDomainTypeHash = Keccak(
            Encoding.UTF8.GetBytes("EIP712Domain(string name,string version,uint256 chainId)"));
        ExchangeDomainTypeHash = Keccak(
            Encoding.UTF8.GetBytes("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"));
        ClobAuthTypeHash = Keccak(
            Encoding.UTF8.GetBytes("ClobAuth(address address,string timestamp,uint256 nonce,string message)"));
        OrderTypeHash = Keccak(
            Encoding.UTF8.GetBytes("Order(uint256 salt,address maker,address signer,address taker,uint256 tokenId,uint256 makerAmount,uint256 takerAmount,uint256 expiration,uint256 nonce,uint256 feeRateBps,uint8 side,uint8 signatureType)"));
    }

    private readonly string _host;
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly EthECKey _ecKey;
    private readonly string _signerAddress;
    private readonly string _funderAddress;
    private readonly int _chainId;
    private readonly int _signatureType;

    // Cached domain separators
    private readonly byte[] _authDomainSep;
    private readonly byte[] _exchangeDomainSep;
    private readonly byte[] _negRiskExchangeDomainSep;

    // API credentials (populated by InitializeAsync)
    private string _apiKey = "";
    private string _apiSecret = "";
    private string _apiPassphrase = "";

    // Exchange addresses (needed for setApprovalForAll on CTF)
    private readonly string _exchangeAddress;
    private readonly string _negRiskExchangeAddress;

    // Auto-claim: Polygon RPC + CTF contract
    private readonly string _privateKey;
    private readonly string _polygonRpcUrl;
    private readonly string _ctfAddress;
    private readonly string _usdcAddress;

    public string ApiKey => _apiKey;
    public bool IsInitialized => !string.IsNullOrEmpty(_apiKey);

    public ClobApiClient(BotConfig config, HttpClient http, ILogger log)
    {
        _host = config.ClobHost;
        _http = http;
        _log = log;
        _chainId = config.PolymarketChainId;
        _signatureType = config.PolymarketSignatureType;

        _ecKey = new EthECKey(config.PolymarketPrivateKey);
        _signerAddress = _ecKey.GetPublicAddress();
        _funderAddress = string.IsNullOrEmpty(config.PolymarketFunderAddress)
            ? _signerAddress
            : config.PolymarketFunderAddress;

        _authDomainSep = ComputeAuthDomainSeparator(_chainId);
        _exchangeAddress = config.ExchangeAddress;
        _negRiskExchangeAddress = config.NegRiskExchangeAddress;
        _exchangeDomainSep = ComputeExchangeDomainSeparator(_chainId, _exchangeAddress);
        _negRiskExchangeDomainSep = ComputeExchangeDomainSeparator(_chainId, _negRiskExchangeAddress);

        _privateKey = config.PolymarketPrivateKey;
        _polygonRpcUrl = config.PolygonRpcUrl;
        _ctfAddress = config.CtfAddress;
        _usdcAddress = config.UsdcAddress;

        // Use pre-configured creds if available
        if (!string.IsNullOrEmpty(config.PolymarketApiKey) &&
            !string.IsNullOrEmpty(config.PolymarketApiSecret))
        {
            _apiKey = config.PolymarketApiKey;
            _apiSecret = config.PolymarketApiSecret;
            _apiPassphrase = config.PolymarketApiPassphrase;
        }

        _log.LogInformation("CLOB client: signer={Signer}, funder={Funder}, sigType={SigType}",
            _signerAddress, _funderAddress, _signatureType);
    }

    // ── Initialization ──────────────────────────────────────────────

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsInitialized)
        {
            _log.LogInformation("Using pre-configured CLOB API credentials");
            return;
        }

        // Derive API creds via L1 auth (EIP-712 signed request)
        try
        {
            await CreateApiKeyAsync(ct);
            _log.LogInformation("Created new CLOB API key: {Key}", _apiKey[..8] + "...");
        }
        catch (Exception ex)
        {
            _log.LogDebug("Create API key failed ({Msg}), trying derive...", ex.Message);
            await DeriveApiKeyAsync(ct);
            _log.LogInformation("Derived existing CLOB API key: {Key}", _apiKey[..8] + "...");
        }
    }

    private async Task CreateApiKeyAsync(CancellationToken ct)
    {
        var headers = BuildL1Headers();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_host}/auth/api-key");
        foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);

        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();
        ParseApiCreds(body);
    }

    private async Task DeriveApiKeyAsync(CancellationToken ct)
    {
        var headers = BuildL1Headers();
        var req = new HttpRequestMessage(HttpMethod.Get, $"{_host}/auth/derive-api-key");
        foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);

        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();
        ParseApiCreds(body);
    }

    private void ParseApiCreds(string json)
    {
        var doc = JsonDocument.Parse(json);
        _apiKey = doc.RootElement.GetProperty("apiKey").GetString()!;
        _apiSecret = doc.RootElement.GetProperty("secret").GetString()!;
        _apiPassphrase = doc.RootElement.GetProperty("passphrase").GetString()!;
    }

    // ── Order posting ───────────────────────────────────────────────

    /// <summary>
    /// Result of a CLOB order submission. Contains actual fill amounts when matched.
    /// IsMatched=true means the POST response already contained status="matched" — no polling needed.
    /// </summary>
    public record OrderResult(string OrderId, double ActualCostUsd, double ActualShares, bool IsMatched = false);

    public async Task<OrderResult?> PostMarketBuyOrderAsync(
        string tokenId, double amountUsd, double price, CancellationToken ct)
    {
        // 1. Fetch market metadata
        var tickSize = await GetTickSizeAsync(tokenId, ct);
        var negRisk = await GetNegRiskAsync(tokenId, ct);
        int decimals = GetDecimals(tickSize);

        _log.LogDebug("Order metadata: tickSize={Tick}, negRisk={NegRisk}, decimals={Dec}",
            tickSize, negRisk, decimals);

        // 2. The caller already walked the ask book. Round the exact worst
        //    acceptable level up to a valid tick without adding hidden slippage.
        double tickSizeD = double.Parse(tickSize, System.Globalization.CultureInfo.InvariantCulture);
        double roundedPrice = Math.Ceiling(price / tickSizeD - 1e-9) * tickSizeD;
        roundedPrice = Math.Round(roundedPrice, decimals);
        roundedPrice = Math.Min(roundedPrice, 1.0 - tickSizeD); // never exceed (1 - tick)

        // 3. Calculate amounts (6-decimal USDC units)
        // For GTC BUY: takerAmount = tokens to receive (max 2 dp), makerAmount = USDC to spend (max 4 dp)
        // CLOB derives price = maker/taker and validates it matches tick size
        double rawTaker = Math.Round(amountUsd / roundedPrice, 2);
        double rawMaker = Math.Round(rawTaker * roundedPrice, 4);

        // CLOB enforces minimum order size of 5 tokens
        if (rawTaker < 5.0)
        {
            _log.LogWarning("Order size {Taker:F2} tokens below CLOB minimum of 5 (need ${MinUsd:F2} at price {Price})",
                rawTaker, 5.0 * roundedPrice, roundedPrice);
            return null;
        }

        // Use Math.Round (not Floor) to avoid floating-point precision loss
        long makerAmount = (long)Math.Round(rawMaker * 1_000_000);
        long takerAmount = (long)Math.Round(rawTaker * 1_000_000);

        if (makerAmount <= 0 || takerAmount <= 0)
        {
            _log.LogWarning("Invalid order amounts: maker={Maker}, taker={Taker}", makerAmount, takerAmount);
            return null;
        }

        // 3. Build order struct
        long salt = GenerateSalt();
        var domainSep = negRisk ? _negRiskExchangeDomainSep : _exchangeDomainSep;

        // 4. Sign order with EIP-712 (direct ECDSA, no personal sign prefix)
        var orderFields = new OrderFields
        {
            Salt = salt,
            Maker = _funderAddress,
            Signer = _signerAddress,
            Taker = "0x0000000000000000000000000000000000000000",
            TokenId = tokenId,
            MakerAmount = makerAmount,
            TakerAmount = takerAmount,
            Expiration = 0,
            Nonce = 0,
            FeeRateBps = 0,
            Side = 0, // BUY = 0
            SignatureType = _signatureType,
        };

        var signature = SignOrder(orderFields, domainSep);
        var sigHex = "0x" + Convert.ToHexString(signature).ToLowerInvariant();

        // 5. Build JSON body (compact, matching py-clob-client format)
        var body = new Dictionary<string, object>
        {
            ["order"] = new Dictionary<string, object>
            {
                ["salt"] = salt,
                ["maker"] = _funderAddress,
                ["signer"] = _signerAddress,
                ["taker"] = "0x0000000000000000000000000000000000000000",
                ["tokenId"] = tokenId,
                ["makerAmount"] = makerAmount.ToString(),
                ["takerAmount"] = takerAmount.ToString(),
                ["expiration"] = "0",
                ["nonce"] = "0",
                ["feeRateBps"] = "0",
                ["side"] = "BUY",
                ["signatureType"] = _signatureType,
                ["signature"] = sigHex,
            },
            ["owner"] = _apiKey,
            ["orderType"] = "GTC",
        };

        var bodyJson = JsonSerializer.Serialize(body, _jsonOpts);
        _log.LogDebug("Order body: {Body}", bodyJson[..Math.Min(bodyJson.Length, 200)]);

        // 6. Post with L2 HMAC auth
        var l2Headers = BuildL2Headers("POST", "/order", bodyJson);
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_host}/order")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        foreach (var h in l2Headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);

        var resp = await _http.SendAsync(req, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("CLOB order failed: {Status} {Body}", resp.StatusCode, respText);
            throw new ClobOrderRejectedException(resp.StatusCode, respText[..Math.Min(respText.Length, 500)]);
        }

        _log.LogInformation("CLOB order response: {Resp}", respText[..Math.Min(respText.Length, 200)]);

        var respDoc = JsonDocument.Parse(respText);
        var orderId = respDoc.RootElement.TryGetProperty("orderID", out var oid) ? oid.GetString()
             : respDoc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString()
             : Guid.NewGuid().ToString();

        // Parse actual fill amounts from CLOB response (may differ from requested due to price improvement)
        double actualCost = amountUsd; // fallback to requested
        double actualShares = amountUsd / price;
        if (respDoc.RootElement.TryGetProperty("makingAmount", out var making) &&
            double.TryParse(making.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var makingVal) &&
            makingVal > 0)
        {
            actualCost = makingVal;
        }
        if (respDoc.RootElement.TryGetProperty("takingAmount", out var taking) &&
            double.TryParse(taking.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var takingVal) &&
            takingVal > 0)
        {
            actualShares = takingVal;
        }

        bool isMatched = respDoc.RootElement.TryGetProperty("status", out var statusEl) &&
                         statusEl.GetString()?.Equals("matched", StringComparison.OrdinalIgnoreCase) == true;

        return new OrderResult(orderId!, actualCost, actualShares, isMatched);
    }

    /// <summary>
    /// Post a GTC SELL order: selling shares (conditional tokens) for USDC.
    /// For SELL: makerAmount = tokens (what we give), takerAmount = USDC (what we receive).
    /// Returns OrderResult with actual fill data, or null on failure.
    /// </summary>
    public async Task<OrderResult?> PostMarketSellOrderAsync(
        string tokenId, double shares, double price, CancellationToken ct)
    {
        // Refresh CLOB cache and get actual on-chain token balance.
        // GTC BUY orders can partially fill — the portfolio may record more shares
        // than actually settled on-chain (e.g. 17.29 recorded but only 7.29 on-chain).
        // Use the actual balance to avoid "not enough balance / allowance" errors.
        var actualBalance = await GetActualConditionalBalanceAsync(tokenId, ct);
        if (actualBalance.HasValue && actualBalance.Value < shares - 0.000001)
        {
            _log.LogWarning("Partial-fill: portfolio={Portfolio:F6} tokens, on-chain={OnChain:F6} (diff={Diff:F6}); using on-chain amount",
                shares, actualBalance.Value, shares - actualBalance.Value);
            shares = actualBalance.Value;
        }

        var tickSize = await GetTickSizeAsync(tokenId, ct);
        var negRisk = await GetNegRiskAsync(tokenId, ct);
        int decimals = GetDecimals(tickSize);

        _log.LogInformation("[SELL-META] token={Token} tickSize={Tick} negRisk={NegRisk} signatureType={SigType} funder={Funder}",
            tokenId[..12], tickSize, negRisk, _signatureType, _funderAddress[..10]);

        double tickSizeD = double.Parse(tickSize, CultureInfo.InvariantCulture);

        // The caller already walked the bid book; use its worst acceptable level.
        double roundedPrice = Math.Floor(price / tickSizeD + 1e-9) * tickSizeD;
        roundedPrice = Math.Round(roundedPrice, decimals);
        roundedPrice = Math.Max(roundedPrice, tickSizeD); // never go below 1 tick

        // Safety net: price below tick size rounds to 0 → can't create valid order
        if (roundedPrice <= 0)
        {
            _log.LogWarning("SELL price {Price:F6} rounds to 0 at tick size {Tick} — too low to sell", price, tickSize);
            return null;
        }

        // For SELL: makerAmount = tokens to sell (max 2 dp), takerAmount = USDC to receive (max 4 dp)
        // Use Floor (not Round) so we never request more tokens than we actually hold.
        // Math.Round can round 11.076921 → 11.08, exceeding the on-chain balance by a few atomic units.
        double rawMaker = Math.Floor(shares * 100) / 100;  // floor to 2dp

        if (rawMaker < 5.0)
        {
            _log.LogWarning("SELL order size {Shares:F2} tokens below CLOB minimum of 5", rawMaker);
            return null;
        }

        long makerAmount = (long)Math.Round(rawMaker * 1_000_000);
        // Compute takerAmount via integer tick arithmetic so the ratio takerAmount/makerAmount is
        // exactly tick-aligned. Float approach (rawMaker * roundedPrice * 1e6) introduces rounding
        // error: e.g. 572400 / 6290000 = 0.09100159 which CLOB rejects.
        // Integer approach: 6290000 * 91 / 1000 = 572390 → 572390/6290000 = 0.091 exactly.
        long ticksPerUnit = (long)Math.Round(1.0 / tickSizeD);
        long priceInTicks = (long)Math.Round(roundedPrice / tickSizeD);
        long takerAmount = makerAmount * priceInTicks / ticksPerUnit;

        if (makerAmount <= 0 || takerAmount <= 0)
        {
            _log.LogWarning("Invalid SELL amounts: maker={Maker}, taker={Taker} (price too low?)", makerAmount, takerAmount);
            return null;
        }

        long salt = GenerateSalt();
        var domainSep = negRisk ? _negRiskExchangeDomainSep : _exchangeDomainSep;

        var orderFields = new OrderFields
        {
            Salt = salt,
            Maker = _funderAddress,
            Signer = _signerAddress,
            Taker = "0x0000000000000000000000000000000000000000",
            TokenId = tokenId,
            MakerAmount = makerAmount,
            TakerAmount = takerAmount,
            Expiration = 0,
            Nonce = 0,
            FeeRateBps = 0,
            Side = 1, // SELL = 1
            SignatureType = _signatureType,
        };

        var signature = SignOrder(orderFields, domainSep);
        var sigHex = "0x" + Convert.ToHexString(signature).ToLowerInvariant();

        var body = new Dictionary<string, object>
        {
            ["order"] = new Dictionary<string, object>
            {
                ["salt"] = salt,
                ["maker"] = _funderAddress,
                ["signer"] = _signerAddress,
                ["taker"] = "0x0000000000000000000000000000000000000000",
                ["tokenId"] = tokenId,
                ["makerAmount"] = makerAmount.ToString(),
                ["takerAmount"] = takerAmount.ToString(),
                ["expiration"] = "0",
                ["nonce"] = "0",
                ["feeRateBps"] = "0",
                ["side"] = "SELL",
                ["signatureType"] = _signatureType,
                ["signature"] = sigHex,
            },
            ["owner"] = _apiKey,
            ["orderType"] = "GTC",
        };

        var bodyJson = JsonSerializer.Serialize(body, _jsonOpts);
        _log.LogDebug("SELL order body: {Body}", bodyJson[..Math.Min(bodyJson.Length, 200)]);

        var l2Headers = BuildL2Headers("POST", "/order", bodyJson);
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_host}/order")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        foreach (var h in l2Headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);

        var resp = await _http.SendAsync(req, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("CLOB SELL order failed: {Status} {Body}", resp.StatusCode, respText);
            throw new ClobOrderRejectedException(resp.StatusCode, respText[..Math.Min(respText.Length, 500)]);
        }

        _log.LogInformation("CLOB SELL order response: {Resp}", respText[..Math.Min(respText.Length, 200)]);

        var respDoc = JsonDocument.Parse(respText);
        var orderId = respDoc.RootElement.TryGetProperty("orderID", out var oid) ? oid.GetString()
             : respDoc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString()
             : Guid.NewGuid().ToString();

        // For SELL: makingAmount = tokens sold, takingAmount = USDC received
        double actualShares = shares;
        double actualCost = shares * price;
        if (respDoc.RootElement.TryGetProperty("makingAmount", out var making) &&
            double.TryParse(making.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var makingVal) &&
            makingVal > 0)
        {
            actualShares = makingVal;
        }
        if (respDoc.RootElement.TryGetProperty("takingAmount", out var taking) &&
            double.TryParse(taking.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var takingVal) &&
            takingVal > 0)
        {
            actualCost = takingVal;
        }

        bool isMatched = respDoc.RootElement.TryGetProperty("status", out var statusEl) &&
                         statusEl.GetString()?.Equals("matched", StringComparison.OrdinalIgnoreCase) == true;

        return new OrderResult(orderId!, actualCost, actualShares, isMatched);
    }

    // ── Order status & cancel ──────────────────────────────────────

    /// <summary>Read order status together with its final or partial matched amount.</summary>
    public async Task<OrderFill?> GetOrderFillAsync(
        string orderId, string side, double fallbackPrice, CancellationToken ct)
    {
        try
        {
            var l2Headers = BuildL2Headers("GET", $"/data/order/{orderId}");
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_host}/data/order/{orderId}");
            foreach (var h in l2Headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);

            var resp = await _http.SendAsync(req, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("Order status check failed: {Status} {Body}", resp.StatusCode, respText);
                return null;
            }

            var fill = OrderReconciliation.Parse(respText, side, fallbackPrice);
            _log.LogDebug("Order {OrderId} status: {Status}, filled={Shares:F4}",
                orderId[..Math.Min(12, orderId.Length)], fill.Status, fill.Shares);
            return fill;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Order status check error: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Cancel an open GTC order.
    /// </summary>
    public async Task CancelOrderAsync(string orderId, CancellationToken ct)
    {
        try
        {
            var bodyObj = new Dictionary<string, string> { ["orderID"] = orderId };
            var bodyJson = JsonSerializer.Serialize(bodyObj, _jsonOpts);

            var l2Headers = BuildL2Headers("DELETE", "/order", bodyJson);
            var req = new HttpRequestMessage(HttpMethod.Delete, $"{_host}/order")
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };
            foreach (var h in l2Headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);

            var resp = await _http.SendAsync(req, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            _log.LogInformation("Cancel order {OrderId}: {Status} {Body}",
                orderId[..12], resp.StatusCode, respText[..Math.Min(respText.Length, 100)]);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Cancel order error: {Error}", ex.Message);
        }
    }

    // ── Balance query ──────────────────────────────────────────────

    /// <summary>
    /// Tell the CLOB to re-sync its cached balance+allowance for a conditional token
    /// from on-chain state. Must be called before SELL orders so the CLOB knows we
    /// actually hold the tokens and they are approved for the exchange.
    /// Calls GET /balance-allowance/update?asset_type=CONDITIONAL&amp;token_id=...
    /// </summary>
    /// <summary>
    /// Refresh CLOB's cached on-chain state for a conditional token, then read the
    /// actual token balance. Returns the actual number of tokens held (in whole tokens),
    /// or null if the query fails. Used to detect partial-fill discrepancies before SELL.
    /// </summary>
    public async Task<double?> GetActualConditionalBalanceAsync(string tokenId, CancellationToken ct)
    {
        try
        {
            // Step 1: GET /balance-allowance/update to refresh CLOB cache from chain
            var updatePath = "/balance-allowance/update";
            var updateUrl = $"{_host}{updatePath}?asset_type=CONDITIONAL&token_id={tokenId}&signature_type={_signatureType}";
            var updateHeaders = BuildL2Headers("GET", updatePath);
            var updateReq = new HttpRequestMessage(HttpMethod.Get, updateUrl);
            foreach (var h in updateHeaders) updateReq.Headers.TryAddWithoutValidation(h.Key, h.Value);
            var updateResp = await _http.SendAsync(updateReq, ct);
            if (!updateResp.IsSuccessStatusCode)
            {
                var updateBody = await updateResp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("[COND-UPDATE] {Token} failed {Status}: {Body}", tokenId[..12], updateResp.StatusCode, updateBody[..Math.Min(updateBody.Length, 200)]);
            }

            // Step 2: GET /balance-allowance to read actual conditional token balance
            var readPath = "/balance-allowance";
            var readUrl = $"{_host}{readPath}?asset_type=CONDITIONAL&token_id={tokenId}&signature_type={_signatureType}";
            var readHeaders = BuildL2Headers("GET", readPath);
            var readReq = new HttpRequestMessage(HttpMethod.Get, readUrl);
            foreach (var h in readHeaders) readReq.Headers.TryAddWithoutValidation(h.Key, h.Value);
            var readResp = await _http.SendAsync(readReq, ct);
            var readBody = await readResp.Content.ReadAsStringAsync(ct);

            // Balance is in 6-decimal units (same as USDC), e.g. "7290000" = 7.29 tokens
            _log.LogDebug("[COND-BALANCE-RAW] {Token}: {Body}", tokenId[..12], readBody[..Math.Min(readBody.Length, 300)]);
            var doc = JsonDocument.Parse(readBody);
            if (doc.RootElement.TryGetProperty("balance", out var balEl) &&
                long.TryParse(balEl.GetString(), out var balRaw))
            {
                double balance = balRaw / 1_000_000.0;
                _log.LogInformation("[COND-BALANCE] {Token}: {Balance:F2} tokens", tokenId[..12], balance);
                return balance;
            }

            _log.LogWarning("[COND-BALANCE] {Token}: could not parse balance from: {Body}", tokenId[..12], readBody[..Math.Min(readBody.Length, 200)]);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning("[COND-BALANCE] error for {Token}: {Error}", tokenId[..12], ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Fetch USDC collateral balance from the CLOB API (L2 authenticated).
    /// Returns balance in USD, or null on failure.
    /// </summary>
    public async Task<double?> GetBalanceAsync(CancellationToken ct)
    {
        try
        {
            // HMAC signs only the path (no query params) — matching py-clob-client behavior
            var l2Headers = BuildL2Headers("GET", "/balance-allowance");
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_host}/balance-allowance?asset_type=COLLATERAL&signature_type={_signatureType}");
            foreach (var h in l2Headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);

            var resp = await _http.SendAsync(req, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Balance check failed: {Status} {Body}", resp.StatusCode, respText);
                return null;
            }

            var doc = JsonDocument.Parse(respText);
            _log.LogInformation("Balance API response: {Body}", respText);
            Console.WriteLine($"[BALANCE API] raw response: {respText}");
            if (doc.RootElement.TryGetProperty("balance", out var balEl))
            {
                var balStr = balEl.GetString() ?? "0";
                // Balance is in USDC atomic units (6 decimals)
                if (double.TryParse(balStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var rawBal))
                    return rawBal / 1_000_000.0;
            }

            _log.LogWarning("Balance response missing 'balance' field: {Body}", respText);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Balance check error: {Error}", ex.Message);
            return null;
        }
    }

    // ── ERC-1155 setApprovalForAll ──────────────────────────────────

    /// <summary>
    /// Ensure the CTF contract has approved both exchange contracts as operators
    /// (required for SELL orders — the exchange needs to transfer conditional tokens).
    /// Checks isApprovedForAll first; only sends tx if not already approved.
    /// Requires ctf_address, exchange_address, and polygon_rpc_url in config.
    /// </summary>
    public async Task<bool> EnsureConditionalTokenApprovalsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_ctfAddress) || string.IsNullOrEmpty(_polygonRpcUrl))
        {
            _log.LogWarning("CTF approval check skipped — ctf_address / polygon_rpc_url not configured");
            return false;
        }

        if (!string.IsNullOrEmpty(_exchangeAddress))
            await ApproveIfNeededAsync(_ctfAddress, _exchangeAddress, "Exchange", ct);

        if (!string.IsNullOrEmpty(_negRiskExchangeAddress))
            await ApproveIfNeededAsync(_ctfAddress, _negRiskExchangeAddress, "NegRiskExchange", ct);

        return true;
    }

    private async Task ApproveIfNeededAsync(
        string ctfContract, string operatorAddr, string label, CancellationToken ct)
    {
        try
        {
            bool approved = await IsApprovedForAllAsync(ctfContract, _signerAddress, operatorAddr, ct);
            if (approved)
            {
                _log.LogInformation("CTF approval OK for {Label} ({Operator})", label, operatorAddr[..10]);
                return;
            }

            _log.LogInformation("CTF not approved for {Label} — sending setApprovalForAll tx...", label);
            var txHash = await SendSetApprovalForAllAsync(ctfContract, operatorAddr, true, ct);
            _log.LogInformation("setApprovalForAll tx sent for {Label}: {TxHash}", label, txHash);
        }
        catch (Exception ex)
        {
            _log.LogWarning("CTF approval check/set failed for {Label}: {Error}", label, ex.Message);
        }
    }

    /// <summary>
    /// Call CTF.isApprovedForAll(owner, operator) via eth_call (read-only).
    /// </summary>
    private async Task<bool> IsApprovedForAllAsync(
        string ctfContract, string owner, string operatorAddr, CancellationToken ct)
    {
        // selector = keccak256("isApprovedForAll(address,address)")[:4] = 0xe985e9c5
        var selector = Keccak(Encoding.UTF8.GetBytes(
            "isApprovedForAll(address,address)")).Take(4).ToArray();

        var callData = new byte[4 + 2 * 32];
        Array.Copy(selector, 0, callData, 0, 4);
        Array.Copy(AbiEncodeAddress(owner), 0, callData, 4, 32);
        Array.Copy(AbiEncodeAddress(operatorAddr), 0, callData, 36, 32);

        var dataHex = "0x" + Convert.ToHexString(callData).ToLowerInvariant();

        var result = await PolygonRpcCallAsync<string>(
            "eth_call",
            new object[] {
                new { to = ctfContract, data = dataHex },
                "latest"
            }, ct);

        // Result is 32 bytes: 0 = false, 1 = true
        var clean = result.StartsWith("0x") ? result[2..] : result;
        return clean.TrimStart('0').Length > 0 && clean.TrimStart('0') != "0";
    }

    /// <summary>
    /// Send CTF.setApprovalForAll(operator, approved) as an on-chain transaction.
    /// </summary>
    private async Task<string> SendSetApprovalForAllAsync(
        string ctfContract, string operatorAddr, bool approved, CancellationToken ct)
    {
        // selector = keccak256("setApprovalForAll(address,bool)")[:4] = 0xa22cb465
        var selector = Keccak(Encoding.UTF8.GetBytes(
            "setApprovalForAll(address,bool)")).Take(4).ToArray();

        var callData = new byte[4 + 2 * 32];
        Array.Copy(selector, 0, callData, 0, 4);
        Array.Copy(AbiEncodeAddress(operatorAddr), 0, callData, 4, 32);
        Array.Copy(AbiEncodeUint256(approved ? 1 : 0), 0, callData, 36, 32);

        var nonceHex = await PolygonRpcCallAsync<string>(
            "eth_getTransactionCount", new object[] { _signerAddress, "latest" }, ct);
        var nonce = BigInteger.Parse(
            nonceHex.TrimStart('0').Length == 0 ? "0" :
            nonceHex.StartsWith("0x") ? nonceHex[2..] : nonceHex,
            NumberStyles.HexNumber);

        var gasPriceHex = await PolygonRpcCallAsync<string>(
            "eth_gasPrice", Array.Empty<object>(), ct);
        var gasPrice = BigInteger.Parse(
            gasPriceHex.StartsWith("0x") ? gasPriceHex[2..] : gasPriceHex,
            NumberStyles.HexNumber);
        gasPrice = gasPrice * 12 / 10; // 20% buffer

        var gasLimit = new BigInteger(100_000);

        var signedTxBytes = BuildSignedEip155Transaction(
            new BigInteger(_chainId), ctfContract, BigInteger.Zero,
            nonce, gasPrice, gasLimit, callData);
        var signedTxHex = "0x" + Convert.ToHexString(signedTxBytes).ToLowerInvariant();

        return await PolygonRpcCallAsync<string>(
            "eth_sendRawTransaction", new object[] { signedTxHex }, ct);
    }

    // ── L1 Headers (EIP-712 ClobAuth + Ethereum Personal Sign) ──────

    private Dictionary<string, string> BuildL1Headers(int nonce = 0)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Encode ClobAuth struct
        var structData = new byte[32 * 5];
        Array.Copy(ClobAuthTypeHash, 0, structData, 0, 32);
        Array.Copy(AbiEncodeAddress(_signerAddress), 0, structData, 32, 32);
        Array.Copy(Keccak(Encoding.UTF8.GetBytes(timestamp.ToString())), 0, structData, 64, 32);
        Array.Copy(AbiEncodeUint256(nonce), 0, structData, 96, 32);
        Array.Copy(Keccak(Encoding.UTF8.GetBytes(AuthMessage)), 0, structData, 128, 32);
        var structHash = Keccak(structData);

        // EIP-712 digest: keccak256(0x1901 || domainSep || structHash)
        var signable = new byte[2 + 32 + 32];
        signable[0] = 0x19;
        signable[1] = 0x01;
        Array.Copy(_authDomainSep, 0, signable, 2, 32);
        Array.Copy(structHash, 0, signable, 34, 32);
        var eip712Digest = Keccak(signable);

        // py-clob-client uses Account._sign_hash (direct ECDSA, no personal sign prefix)
        var signature = EcdsaSign(eip712Digest);

        return new Dictionary<string, string>
        {
            ["POLY_ADDRESS"] = _signerAddress,
            ["POLY_SIGNATURE"] = "0x" + Convert.ToHexString(signature).ToLowerInvariant(),
            ["POLY_TIMESTAMP"] = timestamp.ToString(),
            ["POLY_NONCE"] = nonce.ToString(),
        };
    }

    // ── L2 Headers (HMAC-SHA256) ────────────────────────────────────

    private Dictionary<string, string> BuildL2Headers(string method, string path, string? body = null)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // HMAC message: timestamp + method + path [+ body]
        var message = $"{timestamp}{method}{path}";
        if (!string.IsNullOrEmpty(body))
            message += body;

        // Decode base64url API secret, compute HMAC-SHA256
        var secretBytes = Base64UrlDecode(_apiSecret);
        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        var signature = Base64UrlEncode(hash);

        return new Dictionary<string, string>
        {
            ["POLY_ADDRESS"] = _signerAddress,
            ["POLY_SIGNATURE"] = signature,
            ["POLY_TIMESTAMP"] = timestamp.ToString(),
            ["POLY_API_KEY"] = _apiKey,
            ["POLY_PASSPHRASE"] = _apiPassphrase,
        };
    }

    // ── Order EIP-712 Signing (direct ECDSA, no personal prefix) ────

    private byte[] SignOrder(OrderFields o, byte[] domainSep)
    {
        // Encode all 12 order fields
        var data = new byte[32 * 13]; // typeHash + 12 fields
        int pos = 0;

        void Write(byte[] src) { Array.Copy(src, 0, data, pos, 32); pos += 32; }

        Write(OrderTypeHash);
        Write(AbiEncodeUint256(o.Salt));
        Write(AbiEncodeAddress(o.Maker));
        Write(AbiEncodeAddress(o.Signer));
        Write(AbiEncodeAddress(o.Taker));
        Write(AbiEncodeUint256(BigInteger.Parse(o.TokenId)));
        Write(AbiEncodeUint256(o.MakerAmount));
        Write(AbiEncodeUint256(o.TakerAmount));
        Write(AbiEncodeUint256(o.Expiration));
        Write(AbiEncodeUint256(o.Nonce));
        Write(AbiEncodeUint256(o.FeeRateBps));
        Write(AbiEncodeUint8(o.Side));
        Write(AbiEncodeUint8(o.SignatureType));

        var structHash = Keccak(data);

        // EIP-712 digest
        var signable = new byte[2 + 32 + 32];
        signable[0] = 0x19;
        signable[1] = 0x01;
        Array.Copy(domainSep, 0, signable, 2, 32);
        Array.Copy(structHash, 0, signable, 34, 32);
        var digest = Keccak(signable);

        // Direct ECDSA sign (orders use _sign_hash, not personal sign)
        return EcdsaSign(digest);
    }

    // ── CLOB API metadata endpoints ─────────────────────────────────

    private async Task<string> GetTickSizeAsync(string tokenId, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetStringAsync($"{_host}/tick-size?token_id={tokenId}", ct);
            var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("minimum_tick_size", out var ts))
            {
                if (ts.ValueKind == JsonValueKind.String) return ts.GetString() ?? "0.01";
                if (ts.ValueKind == JsonValueKind.Number) return ts.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            // Some responses return just the value
            if (doc.RootElement.ValueKind == JsonValueKind.String)
                return doc.RootElement.GetString() ?? "0.01";
            if (doc.RootElement.ValueKind == JsonValueKind.Number)
                return doc.RootElement.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "0.01";
        }
        catch (Exception ex)
        {
            _log.LogDebug("Failed to get tick size for {Token}: {Err}", tokenId[..8], ex.Message);
            return "0.01";
        }
    }

    private async Task<bool> GetNegRiskAsync(string tokenId, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetStringAsync($"{_host}/neg-risk?token_id={tokenId}", ct);
            var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("neg_risk", out var nr))
                return nr.GetBoolean();
            if (doc.RootElement.ValueKind == JsonValueKind.True || doc.RootElement.ValueKind == JsonValueKind.False)
                return doc.RootElement.GetBoolean();
            return false;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Failed to get neg_risk for {Token}: {Err}", tokenId[..8], ex.Message);
            return false;
        }
    }

    // ── Crypto primitives ───────────────────────────────────────────

    private static byte[] Keccak(byte[] data)
    {
        return Sha3Keccack.Current.CalculateHash(data);
    }

    private byte[] EcdsaSign(byte[] hash)
    {
        var sig = _ecKey.SignAndCalculateV(hash);
        var result = new byte[65];
        // sig.R and sig.S are byte[] in Nethereum 5.x, left-pad to 32 bytes
        Buffer.BlockCopy(sig.R, 0, result, 32 - sig.R.Length, sig.R.Length);
        Buffer.BlockCopy(sig.S, 0, result, 64 - sig.S.Length, sig.S.Length);
        result[64] = (byte)sig.V[0];
        return result;
    }

    private byte[] PersonalSign(byte[] message)
    {
        // Ethereum personal sign: "\x19Ethereum Signed Message:\n{length}" + message
        var prefix = Encoding.UTF8.GetBytes($"\x19Ethereum Signed Message:\n{message.Length}");
        var fullMessage = new byte[prefix.Length + message.Length];
        Buffer.BlockCopy(prefix, 0, fullMessage, 0, prefix.Length);
        Buffer.BlockCopy(message, 0, fullMessage, prefix.Length, message.Length);
        var hash = Keccak(fullMessage);
        return EcdsaSign(hash);
    }

    // ── ABI encoding helpers ────────────────────────────────────────

    private static byte[] AbiEncodeAddress(string address)
    {
        var hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        var bytes = HexToBytes(hex);
        var result = new byte[32];
        Buffer.BlockCopy(bytes, 0, result, 32 - bytes.Length, bytes.Length);
        return result;
    }

    private static byte[] AbiEncodeUint256(long value)
        => AbiEncodeUint256(new BigInteger(value));

    private static byte[] AbiEncodeUint256(BigInteger value)
    {
        var result = new byte[32];
        if (value > 0)
        {
            var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            Buffer.BlockCopy(bytes, 0, result, 32 - bytes.Length, bytes.Length);
        }
        return result;
    }

    private static byte[] AbiEncodeUint8(int value)
    {
        var result = new byte[32];
        result[31] = (byte)value;
        return result;
    }

    // ── Domain separator computation ────────────────────────────────

    private static byte[] ComputeAuthDomainSeparator(int chainId)
    {
        // EIP712Domain(string name, string version, uint256 chainId)
        var data = new byte[32 * 4];
        Array.Copy(AuthDomainTypeHash, 0, data, 0, 32);
        Array.Copy(Keccak(Encoding.UTF8.GetBytes(AuthDomainName)), 0, data, 32, 32);
        Array.Copy(Keccak(Encoding.UTF8.GetBytes(DomainVersion)), 0, data, 64, 32);
        Array.Copy(AbiEncodeUint256(chainId), 0, data, 96, 32);
        return Keccak(data);
    }

    private static byte[] ComputeExchangeDomainSeparator(int chainId, string contractAddr)
    {
        // EIP712Domain(string name, string version, uint256 chainId, address verifyingContract)
        var data = new byte[32 * 5];
        Array.Copy(ExchangeDomainTypeHash, 0, data, 0, 32);
        Array.Copy(Keccak(Encoding.UTF8.GetBytes(ExchangeDomainName)), 0, data, 32, 32);
        Array.Copy(Keccak(Encoding.UTF8.GetBytes(DomainVersion)), 0, data, 64, 32);
        Array.Copy(AbiEncodeUint256(chainId), 0, data, 96, 32);
        Array.Copy(AbiEncodeAddress(contractAddr), 0, data, 128, 32);
        return Keccak(data);
    }

    // ── Utility helpers ─────────────────────────────────────────────

    private static long GenerateSalt()
    {
        // Matches py-clob-client: round(now * random())
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (long)(now * Random.Shared.NextDouble());
    }

    private static int GetDecimals(string tickSize)
    {
        var dot = tickSize.IndexOf('.');
        return dot >= 0 ? tickSize.Length - dot - 1 : 0;
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber);
        return bytes;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        // Keep '=' padding — py-clob-client uses base64.urlsafe_b64encode which preserves it
        return Convert.ToBase64String(input)
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
    };

    // ── Auto-claim ───────────────────────────────────────────────────

    /// <summary>
    /// Calls CTF.redeemPositions on Polygon for a winning position.
    /// Returns the transaction hash on success, null on any failure (never throws).
    /// Requires ctf_address, usdc_address, and polygon_rpc_url in config.
    /// </summary>
    public async Task<string?> RedeemWinningPositionAsync(
        string conditionId, string side, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_ctfAddress) ||
            string.IsNullOrEmpty(_usdcAddress) ||
            string.IsNullOrEmpty(_polygonRpcUrl))
        {
            _log.LogWarning(
                "Auto-claim skipped — ctf_address / usdc_address / polygon_rpc_url not configured");
            return null;
        }

        try
        {
            // YES token = outcome slot 0 → indexSet 1 (bit 0)
            // NO token  = outcome slot 1 → indexSet 2 (bit 1)
            var indexSet = side.Equals("YES", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            var callData = BuildRedeemCallData(conditionId, indexSet);

            // Fetch nonce
            var nonceHex = await PolygonRpcCallAsync<string>(
                "eth_getTransactionCount", new object[] { _signerAddress, "latest" }, ct);
            var nonce = BigInteger.Parse(
                nonceHex.TrimStart('0').Length == 0 ? "0" :
                nonceHex.StartsWith("0x") ? nonceHex[2..] : nonceHex,
                NumberStyles.HexNumber);

            // Fetch gas price + add 20 % buffer
            var gasPriceHex = await PolygonRpcCallAsync<string>(
                "eth_gasPrice", Array.Empty<object>(), ct);
            var gasPrice = BigInteger.Parse(
                gasPriceHex.StartsWith("0x") ? gasPriceHex[2..] : gasPriceHex,
                NumberStyles.HexNumber);
            gasPrice = gasPrice * 12 / 10;

            var gasLimit = new BigInteger(200_000);

            // Sign EIP-155 legacy transaction and RLP-encode
            var signedTxBytes = BuildSignedEip155Transaction(
                new BigInteger(_chainId), _ctfAddress, BigInteger.Zero,
                nonce, gasPrice, gasLimit, callData);
            var signedTxHex = "0x" + Convert.ToHexString(signedTxBytes).ToLowerInvariant();

            // Broadcast
            var txHash = await PolygonRpcCallAsync<string>(
                "eth_sendRawTransaction", new object[] { signedTxHex }, ct);

            _log.LogInformation("Auto-claim submitted: {TxHash}", txHash);
            return txHash;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Auto-claim failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// ABI-encodes the calldata for:
    ///   CTF.redeemPositions(address collateral, bytes32 parentCollectionId, bytes32 conditionId, uint256[] indexSets)
    /// </summary>
    private byte[] BuildRedeemCallData(string conditionId, int indexSet)
    {
        // selector = keccak256("redeemPositions(address,bytes32,bytes32,uint256[])")[:4]
        var selector = Keccak(Encoding.UTF8.GetBytes(
            "redeemPositions(address,bytes32,bytes32,uint256[])")).Take(4).ToArray();

        // Layout (offsets relative to start of encoded args, not including selector):
        //  [0]  address collateral           32 bytes
        //  [32] bytes32 parentCollectionId   32 bytes (zeros)
        //  [64] bytes32 conditionId          32 bytes
        //  [96] uint256 offset→array         32 bytes  = 0x80 (128)
        //  [128] uint256 array.length        32 bytes  = 1
        //  [160] uint256 array[0]            32 bytes  = indexSet
        //  Total args = 6 * 32 = 192 bytes
        var data = new byte[4 + 6 * 32];
        Array.Copy(selector, 0, data, 0, 4);

        // arg0: collateral (USDC address)
        Array.Copy(AbiEncodeAddress(_usdcAddress), 0, data, 4, 32);

        // arg1: parentCollectionId = bytes32(0)   — already zero-initialised

        // arg2: conditionId bytes32
        var condStr = conditionId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? conditionId[2..] : conditionId;
        var condBytes = HexToBytes(condStr);
        // bytes32 is right-padded in ABI for fixed-size byte arrays
        Array.Copy(condBytes, 0, data, 4 + 64, Math.Min(condBytes.Length, 32));

        // arg3: offset to the dynamic array = 128 (0x80)
        Array.Copy(AbiEncodeUint256(128), 0, data, 4 + 96, 32);

        // array length = 1
        Array.Copy(AbiEncodeUint256(1), 0, data, 4 + 128, 32);

        // array[0] = indexSet
        Array.Copy(AbiEncodeUint256(indexSet), 0, data, 4 + 160, 32);

        return data;
    }

    /// <summary>Minimal JSON-RPC 2.0 call to a Polygon (or any EVM) node.</summary>
    private async Task<T> PolygonRpcCallAsync<T>(
        string method, object[] @params, CancellationToken ct)
    {
        var requestJson = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params,
            id = 1,
        }, _jsonOpts);

        using var response = await _http.PostAsync(
            _polygonRpcUrl,
            new StringContent(requestJson, Encoding.UTF8, "application/json"),
            ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("error", out var errEl))
            throw new InvalidOperationException($"RPC error: {errEl.GetRawText()}");

        var result = doc.RootElement.GetProperty("result");
        return JsonSerializer.Deserialize<T>(result.GetRawText(), _jsonOpts)
               ?? throw new InvalidOperationException("Null RPC result");
    }

    // ── EIP-155 transaction signing (no external dependency) ────────

    /// <summary>
    /// Signs and RLP-encodes a legacy (type-0) EIP-155 transaction.
    /// Compatible with Polygon (chain ID 137).
    /// </summary>
    private byte[] BuildSignedEip155Transaction(
        BigInteger chainId, string to, BigInteger value,
        BigInteger nonce, BigInteger gasPrice, BigInteger gasLimit,
        byte[] data)
    {
        // Step 1: RLP-encode unsigned tx for hashing (EIP-155 includes chainId, 0, 0)
        var rlpUnsigned = RlpList(
            RlpInt(nonce),
            RlpInt(gasPrice),
            RlpInt(gasLimit),
            RlpAddr(to),
            RlpInt(value),
            RlpRaw(data),
            RlpInt(chainId),
            RlpInt(BigInteger.Zero),
            RlpInt(BigInteger.Zero));

        // Step 2: Hash + ECDSA sign
        var hash = Keccak(rlpUnsigned);
        var sig = _ecKey.SignAndCalculateV(hash);

        // sig.V[0] is recovery bit (0 or 1); some Nethereum builds return 27/28
        var recovery = sig.V[0] >= 27 ? sig.V[0] - 27 : sig.V[0];
        var v = chainId * 2 + 35 + recovery;

        // Step 3: RLP-encode signed tx
        return RlpList(
            RlpInt(nonce),
            RlpInt(gasPrice),
            RlpInt(gasLimit),
            RlpAddr(to),
            RlpInt(value),
            RlpRaw(data),
            RlpInt(v),
            RlpSigBytes(sig.R),
            RlpSigBytes(sig.S));
    }

    // Minimal RLP encoding helpers
    private static byte[] RlpInt(BigInteger value)
    {
        if (value == 0) return Array.Empty<byte>();
        return value.ToByteArray(isBigEndian: true, isUnsigned: true);
    }

    private static byte[] RlpAddr(string hexAddr)
    {
        var s = hexAddr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? hexAddr[2..] : hexAddr;
        return HexToBytes(s.PadLeft(40, '0'));
    }

    private static byte[] RlpRaw(byte[] data) => data;

    private static byte[] RlpSigBytes(byte[] bytes)
    {
        // Strip leading zeros (minimal encoding), keep at least 1 byte
        int start = 0;
        while (start < bytes.Length - 1 && bytes[start] == 0) start++;
        return bytes[start..];
    }

    private static byte[] RlpList(params byte[][] items)
    {
        // Encode each item as an RLP string, then wrap in a list header
        var encodedItems = items.Select(RlpString).SelectMany(x => x).ToArray();
        return RlpListHeader(encodedItems.Length).Concat(encodedItems).ToArray();
    }

    private static byte[] RlpString(byte[] data)
    {
        if (data.Length == 0) return new byte[] { 0x80 };
        if (data.Length == 1 && data[0] < 0x80) return data;
        return RlpStringHeader(data.Length).Concat(data).ToArray();
    }

    private static byte[] RlpStringHeader(int len)
    {
        if (len <= 55) return new[] { (byte)(0x80 + len) };
        var lb = RlpLenBytes(len);
        return new[] { (byte)(0xb7 + lb.Length) }.Concat(lb).ToArray();
    }

    private static byte[] RlpListHeader(int len)
    {
        if (len <= 55) return new[] { (byte)(0xc0 + len) };
        var lb = RlpLenBytes(len);
        return new[] { (byte)(0xf7 + lb.Length) }.Concat(lb).ToArray();
    }

    private static byte[] RlpLenBytes(int value)
    {
        var result = new List<byte>();
        while (value > 0) { result.Insert(0, (byte)(value & 0xff)); value >>= 8; }
        return result.ToArray();
    }

    // ── Internal types ──────────────────────────────────────────────

    private struct OrderFields
    {
        public long Salt;
        public string Maker;
        public string Signer;
        public string Taker;
        public string TokenId;
        public long MakerAmount;
        public long TakerAmount;
        public long Expiration;
        public long Nonce;
        public long FeeRateBps;
        public int Side;
        public int SignatureType;
    }
}
