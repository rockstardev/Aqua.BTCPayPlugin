using System;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Aqua.BTCPayPlugin.Services;
using BTCPayServer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Client;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;

namespace Aqua.BTCPayPlugin.Controllers;

[Route("~/plugins/{storeId}/aqua")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class AquaController(
    SamrockProtocolHostedService samrockProtocolHostedService,
    PaymentMethodHandlerDictionary handlers,
    ExplorerClientProvider explorerProvider,
    BTCPayWalletProvider walletProvider,
    StoreRepository storeRepo,
    EventAggregator eventAggregator) : Controller
{
    private StoreData StoreData => HttpContext.GetStoreData();

    [HttpGet("import-wallets")]
    public async Task<IActionResult> ImportWallets()
    {
        var model = new ImportWalletsViewModel
        {
            BtcChain = true,
            BtcLn = false,
            LiquidChain = false,
            LiquidSupportedOnServer = explorerProvider.GetNetwork("LBTC") != null
        };
        return View(model);
    }

    [HttpPost("import-wallets")]
    public async Task<IActionResult> ImportWallets(ImportWalletsViewModel model)
    {
        if (!model.BtcChain && !model.BtcLn && !model.LiquidChain)
        {
            ModelState.AddModelError("", "At least one wallet type must be selected");
            return View(model);
        }

        var random21Charstring = new string(Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Take(21).ToArray());
        model.StoreId = StoreData.Id;
        model.Expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var setupParams = setupParamsFromModel(model);
        var url = $"{baseUrl}/plugins/{model.StoreId}/aqua/samrockprotocol?setup=" +
                  $"{Uri.EscapeDataString(setupParams)}" + 
                  $"&otp={Uri.EscapeDataString(random21Charstring)}";
        model.QrCode = url;
        samrockProtocolHostedService.Add(random21Charstring, model);
        return View(model);
    }

    private string setupParamsFromModel(ImportWalletsViewModel model)
    {
        return
            $"{(model.BtcChain ? "btc-chain," : "")}{(model.LiquidChain ? "liquid-chain," : "")}{(model.BtcLn ? "btc-ln," : "")}";
    }

    [HttpPost("samrockprotocol")]
    public async Task<IActionResult> SamrockProtocol(string otp)
    {
        var jsonField = Request.Form["json"];
        SamrockProtocolModel json;
        try
        {
            json = Newtonsoft.Json.JsonConvert.DeserializeObject<SamrockProtocolModel>(jsonField);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = "Invalid JSON format.", details = ex.Message });
        }

        var importWalletModel = samrockProtocolHostedService.Get(StoreData.Id, otp);
        if (importWalletModel == null)
        {
            return NotFound(new { error = "OTP not found or expired." });
        }

        // only setup onchain for now as proof of concept
        if (importWalletModel.BtcChain)
        {
            try
            {
                var network = explorerProvider.GetNetwork("BTC");
                DerivationSchemeSettings strategy = null;
                PaymentMethodId paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
                strategy = ParseDerivationStrategy(json.BtcChain, network);
                strategy.Source = "ManualDerivationScheme";
                var wallet = walletProvider.GetWallet(network);
                await wallet.TrackAsync(strategy.AccountDerivation);
                StoreData.SetPaymentMethodConfig(handlers[paymentMethodId], strategy);
                var storeBlob = StoreData.GetStoreBlob();
                storeBlob.SetExcluded(paymentMethodId, false);
                storeBlob.PayJoinEnabled = false;
                StoreData.SetStoreBlob(storeBlob);
                await storeRepo.UpdateStore(StoreData);
                eventAggregator.Publish(new WalletChangedEvent
                {
                    WalletId = new WalletId(StoreData.Id, network.CryptoCode)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500,
                    new { error = "An error occurred while setting up OnChain wallet.", details = ex.Message });
            }
        }
        else if (importWalletModel.LiquidChain)
        {
            /*
            xpub...  //confidential but using the proprietary blinding derivation (deterministic based on the xpub and index 
            of address derived, because realistically if server data is leaked, the blinding  key is too, but anyway)
            xpub...-[unblinded] //unconfidential addresses
            xppub-[slip77=master key] //spec variant, master key can be any of the following format:
            * private key in WIF format
            * private key in hex
            * mnemonic phrase that we derive a slip77 master key from
             */
        }

        samrockProtocolHostedService.Remove(StoreData.Id, otp);
        return Ok(new { message = "Wallet setup successfully." });
    }

    // TODO: Copied from BTCPayServer/Controllers/UIStoresController.cs, integrate together
    private DerivationSchemeSettings ParseDerivationStrategy(string derivationScheme, BTCPayNetwork network)
    {
        var parser = new DerivationSchemeParser(network);
        var isOD = Regex.Match(derivationScheme, @"\(.*?\)");
        if (isOD.Success)
        {
            var derivationSchemeSettings = new DerivationSchemeSettings();
            var result = parser.ParseOutputDescriptor(derivationScheme);
            derivationSchemeSettings.AccountOriginal = derivationScheme.Trim();
            derivationSchemeSettings.AccountDerivation = result.Item1;
            derivationSchemeSettings.AccountKeySettings = result.Item2?.Select((path, i) => new AccountKeySettings()
            {
                RootFingerprint = path?.MasterFingerprint,
                AccountKeyPath = path?.KeyPath,
                AccountKey = result.Item1.GetExtPubKeys().ElementAt(i).GetWif(parser.Network)
            }).ToArray() ?? new AccountKeySettings[result.Item1.GetExtPubKeys().Count()];
            return derivationSchemeSettings;
        }

        var strategy = parser.Parse(derivationScheme);
        return new DerivationSchemeSettings(strategy, network);
    }
}

public class ImportWalletsViewModel
{
    public string StoreId { get; set; }
    [DisplayName("Bitcoin")]
    public bool BtcChain { get; set; }
    [DisplayName("Lightning")]
    public bool BtcLn { get; set; }
    [DisplayName("Liquid Bitcoin")]
    public bool LiquidChain { get; set; }
    public string QrCode { get; set; }
    public DateTimeOffset Expires { get; set; }
    public bool LiquidSupportedOnServer { get; set; }
}

public class SamrockProtocolModel
{
    public string BtcChain { get; set; }
    public string BtcLn { get; set; }
    public string LiquidChain { get; set; }
}