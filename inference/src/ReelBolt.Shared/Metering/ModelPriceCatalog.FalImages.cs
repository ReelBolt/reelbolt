using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Metering;

// The fal.ai image endpoints (FalAiImageClient.Catalog) and the SAM 3 segmenters, priced.
//
// HOW A PRICE IS EXPRESSED, because fal's price shapes are not uniform and a rate card keys on a model
// NAME alone. Three rules, applied to every entry below and stated in each entry's basis:
//
//  1. A price that is a pure function of ONE enum parameter (ideogram's `quality`, object-removal's
//     `model`, nano-banana's `resolution`, gpt-image's `quality`) gets one card PER TIER, keyed
//     `{endpoint}#{value}`. FalAiImageClient implements IBillingModelKeyed, and MeteringImageClient
//     records the call under that key, so the tier that actually ran is the tier that is priced.
//  2. A price per megapixel (or per started block of megapixels) cannot be known from a model name.
//     The card is the vendor's figure for the FIRST unit, labelled "from", exactly as the BFL FLUX.2
//     rows are ("a 4 MP request costs MORE than this row"): inventing a reference size here would be the
//     same "assume a duration" move this catalogue refuses to make for the segmenters.
//  3. A token-priced endpoint (Nano Banana 2.1, GPT Image 2.5) is priced from the vendor's own
//     published canonical-size table, the default configuration, and says that thinking and larger
//     sizes cost more.
//
// Every figure was read from fal.ai/models/{id} (the sentence, the llms.txt copy of it, the billing
// metadata embedded in the page, and the gallery listing's pricingInfoOverride) on 2026-10-10.
public static partial class ModelPriceCatalog
{
    private const string FalRead = "published vendor price, fal.ai (fal.ai/models/{0}), read 2026-10-10";

    private static string FalBasis(string id, string statement) =>
        string.Format(FalRead, id) + ": " + statement;

    private static ModelPrice FalSeg(string id, decimal usd, string statement) =>
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, id,
            PerImage(UsageKind.SegmentationImages, usd), FalBasis(id, statement));

    private static ModelPrice FalEdit(string model, string pageId, decimal usd, string statement) =>
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit, model,
            PerImage(UsageKind.ImageEditImages, usd), FalBasis(pageId, statement));

    private static ModelPrice FalGen(string model, string pageId, decimal usd, string statement) =>
        new(InferenceProviderKind.FalAi, InferenceProviderCapability.ImageGeneration, model,
            PerImage(UsageKind.ImageGenImages, usd), FalBasis(pageId, statement));

    private const string FromFirstUnit =
        "; this card is the vendor's figure for the FIRST unit ('from'): a model-keyed card cannot express a " +
        "megapixel count, so a larger image costs MORE than this row";

    private const string FloorNote =
        "; token-priced, so this is the vendor's illustrative figure for a short prompt and EXCLUDES thinking " +
        "tokens (thinking_level defaults to medium and bills as output text) - a floor, not a ceiling";

    private static IEnumerable<ModelPrice> FalImagePrices()
    {
        // ---------------------------------------------------------------- segmentation (per REQUEST)
        yield return FalSeg("fal-ai/sam-3-1/image", 0.01m,
            "\"Your request will cost $0.01 per request.\" (billing metadata: units, 0.01). Billed per REQUEST " +
            "and not per mask: the metering decorator records ONE SegmentationImages unit per SegmentAsync call, " +
            "so a call that returns up to 32 masks is one unit at this price, which is how fal bills it");
        yield return FalSeg("fal-ai/sam-3/image", 0.005m,
            "\"Your request will cost $0.005 per request.\" (billing metadata: units, 0.005). Per request, as " +
            "fal-ai/sam-3-1/image above");

        // ---------------------------------------------------------------- erase
        string removal = "fal-ai/object-removal/mask";
        string removalStatement = "\"Your request will cost $0.006 with Low Quality, $0.012 with Medium Quality, " +
                                  "$0.018 with High Quality, and $0.024 with Best Quality\"; keyed on the `model` tier";
        yield return FalEdit(removal + "#low_quality", removal, 0.006m, removalStatement);
        yield return FalEdit(removal + "#medium_quality", removal, 0.012m, removalStatement);
        yield return FalEdit(removal + "#high_quality", removal, 0.018m, removalStatement);
        yield return FalEdit(removal + "#best_quality", removal, 0.024m, removalStatement + " (the endpoint's default)");

        yield return FalEdit("fal-ai/flux-pro/v1/erase", "fal-ai/flux-pro/v1/erase", 0.042m,
            "\"Your request will cost $0.03 for the first megapixel generated, then $0.004 for each subsequent " +
            "megapixel. Each reference megapixel also costs $0.004 set at 3 MP minimum.\" = $0.03 + 3 x $0.004 " +
            "= $0.042 for the smallest image (derived from that sentence)" + FromFirstUnit);

        // ---------------------------------------------------------------- fill
        yield return FalEdit("fal-ai/flux-pro/v1/fill", "fal-ai/flux-pro/v1/fill", 0.05m,
            "\"Your request will cost $0.05 per megapixel. Images are billed by rounding up to the nearest " +
            "megapixel.\"" + FromFirstUnit);

        string ideoEdit = "ideogram/v4.5/edit";
        string ideoEditStatement = "\"Your request will cost $0.008 per image with Very Low, $0.03 with Low, $0.06 " +
                                   "with Medium, and $0.22 with High quality. The image size does not change the " +
                                   "price.\"; keyed on the `quality` tier";
        yield return FalEdit(ideoEdit + "#very_low", ideoEdit, 0.008m, ideoEditStatement);
        yield return FalEdit(ideoEdit + "#low", ideoEdit, 0.03m, ideoEditStatement);
        yield return FalEdit(ideoEdit + "#medium", ideoEdit, 0.06m, ideoEditStatement + " (the endpoint's default)");
        yield return FalEdit(ideoEdit + "#high", ideoEdit, 0.22m, ideoEditStatement);

        // ---------------------------------------------------------------- Bria FIBO (flat, from billing metadata)
        const string FiboNote = "no price sentence is rendered on the page; the figure is the billing metadata " +
                                "embedded in it (billing_unit images, price 0.04, partner API)";
        yield return FalEdit("bria/fibo-edit/erase_by_text", "bria/fibo-edit/erase_by_text", 0.04m, FiboNote);
        yield return FalEdit("bria/fibo-edit/edit", "bria/fibo-edit/edit", 0.04m, FiboNote);
        yield return FalEdit("bria/fibo-edit/relight", "bria/fibo-edit/relight", 0.04m, FiboNote);

        // ---------------------------------------------------------------- expand / background / relight
        yield return FalEdit("fal-ai/flux-2-pro/outpaint", "fal-ai/flux-2-pro/outpaint", 0.03m,
            "\"Your request will cost $0.03 for the first megapixel of output, plus $0.015 per extra megapixel " +
            "of input and output, rounded up to the nearest megapixel.\"" + FromFirstUnit);
        yield return FalEdit("fal-ai/bria/expand", "fal-ai/bria/expand", 0.04m,
            "\"Your request will cost $0.04 per generation.\" (billing metadata: billing_unit generations, price " +
            "0.04). A flat price per call, whatever the canvas size, so this card is exact");
        yield return FalEdit("fal-ai/bria/background/replace", "fal-ai/bria/background/replace", 0.04m,
            "\"Your request will cost $0.04 per generation.\"");
        yield return FalEdit("fal-ai/image-apps-v2/relighting", "fal-ai/image-apps-v2/relighting", 0.04m,
            "\"Your request will cost $0.04 per image.\"");
        yield return FalEdit("fal-ai/iclight-v2", "fal-ai/iclight-v2", 0.1m,
            "\"Your request will cost $0.1 per megapixel.\"" + FromFirstUnit);

        // ---------------------------------------------------------------- upscale
        yield return FalEdit("fal-ai/seedvr/upscale/image", "fal-ai/seedvr/upscale/image", 0.001m,
            "\"Your request will cost $0.001 per megapixel.\" (the page does not say whether input or output " +
            "megapixels count; a 2x upscale of a 1280x720 image is about 3.7 MP of output)" + FromFirstUnit);
        yield return FalEdit("topaz/upscale/image/precision", "topaz/upscale/image/precision", 0.08m,
            "\"Your request will cost $0.08 per started 24 megapixels of output for every precision model.\" - " +
            "one unit covers any output up to 24 MP, which includes a 4x upscale of a 1280x720 thumbnail (14.7 MP)");
        yield return FalEdit("topaz/upscale/image/generative", "topaz/upscale/image/generative", 0.08m,
            "\"Your request will cost $0.08 per started 8 megapixels of output with Wonder 3 or Wonder 3.5, and " +
            "$0.08 per started 4 megapixels with Wonder, Wonder 2, Recover 3, Standard MAX, Redefine, Recovery, or " +
            "Recovery V2.\" The default model is Wonder 3 (one unit per 8 MP)" + FromFirstUnit);

        // ---------------------------------------------------------------- instruction edit
        string nanoEdit = "google/nano-banana-2.1/edit";
        string nanoEditStatement =
            "pricing is token-based (text/thinking $1.764 in / $8.823 out per 1M; image $1.764 in / $35.294 out per " +
            "1M); the page's own table for \"Edit with two 1K input images\" and a ~100-token prompt gives $0.044 " +
            "(1K), $0.063 (2K) and $0.138 (4K); keyed on the `resolution` tier" + FloorNote;
        yield return FalEdit(nanoEdit + "#1K", nanoEdit, 0.044m, nanoEditStatement + " (the endpoint's default)");
        yield return FalEdit(nanoEdit + "#2K", nanoEdit, 0.063m, nanoEditStatement);
        yield return FalEdit(nanoEdit + "#4K", nanoEdit, 0.138m, nanoEditStatement);

        yield return FalEdit("blackforestlabs/flux-3/edit-image", "blackforestlabs/flux-3/edit-image", 0.048m,
            "\"Image pricing varies by resolution, starting at $0.0205 per image. A 1K (1 MP) image costs $0.024. " +
            "Note: these promotional launch rates are 50% off for a limited time. The discount ends October 8, " +
            "after which a 1K (1 MP) image will cost $0.048\" - the discount had ended by 2026-10-10, so the " +
            "post-discount figure is the card; the client forwards only the `1k` tier, the one tier the page prices");

        foreach (string family in new[] { "sunburst", "flare" })
        {
            string gpt = "openai/gpt-image-2.5/" + family + "/edit";
            string gptStatement =
                "pricing is token-based (text $5.00 in / $1.25 cached / $10.00 out per 1M; image $8.00 in / $2.00 " +
                "cached / $30.00 out per 1M); the page's own canonical-size table at 1920x1080 gives low $0.00441, " +
                "medium $0.01029, high $0.03960, xhigh $0.07041 and max $0.15840; keyed on the `quality` tier. " +
                "Larger sizes cost more (the same table: 3840x2160 high is $0.10008); 1920x1080 is the closest " +
                "published canonical size to a thumbnail";
            yield return FalEdit(gpt + "#low", gpt, 0.00441m, gptStatement);
            yield return FalEdit(gpt + "#medium", gpt, 0.01029m, gptStatement);
            yield return FalEdit(gpt + "#high", gpt, 0.0396m, gptStatement + " (the endpoint's default)");
            yield return FalEdit(gpt + "#xhigh", gpt, 0.07041m, gptStatement);
            yield return FalEdit(gpt + "#max", gpt, 0.1584m, gptStatement);
        }

        yield return FalEdit("bytedance/seedream/v5/pro/edit", "bytedance/seedream/v5/pro/edit", 0.0675m,
            "the page sentence reads \"$0.03375 per generated layer for total pixel area under 1536x1536 ... " +
            "$0.0675 per generated layer\" over it, while the gallery listing reads \"Tentative pricing is $0.0675 " +
            "+ $(0.0045 x number of additional input images) per output image\" up to 1536x1536 and $0.135 above; " +
            "the two disagree by 2x, so the HIGHER ($0.0675) is the card. The client forwards only " +
            "image_size=auto_1K so output stays in the lower area tier, and sends one input image");
        yield return FalEdit("fal-ai/qwen-image-edit-2511", "fal-ai/qwen-image-edit-2511", 0.03m,
            "no price sentence is rendered; the page's llms.txt reads \"Price: $0.03 per megapixels\" and the " +
            "billing metadata is billing_unit megapixels, price 0.03" + FromFirstUnit);
        yield return FalEdit("fal-ai/image-editing/youtube-thumbnails", "fal-ai/image-editing/youtube-thumbnails", 0.04m,
            "\"Your request will cost $0.04 per image.\"");

        // ---------------------------------------------------------------- generation
        string ideo = "ideogram/v4.5";
        string ideoStatement = "\"Your request will cost $0.03 per image with Low, $0.06 with Medium, and $0.22 with " +
                               "High quality. The image size does not change the price.\"; keyed on the `quality` tier";
        yield return FalGen(ideo + "#low", ideo, 0.03m, ideoStatement);
        yield return FalGen(ideo + "#medium", ideo, 0.06m, ideoStatement + " (the endpoint's default)");
        yield return FalGen(ideo + "#high", ideo, 0.22m, ideoStatement);

        string nano = "google/nano-banana-2.1";
        string nanoStatement =
            "pricing is token-based (same rates as the edit endpoint); the page's own table for text-to-image and a " +
            "~100-token prompt gives $0.040 (1K), $0.059 (2K) and $0.134 (4K); keyed on the `resolution` tier" + FloorNote;
        yield return FalGen(nano + "#1K", nano, 0.04m, nanoStatement + " (the endpoint's default)");
        yield return FalGen(nano + "#2K", nano, 0.059m, nanoStatement);
        yield return FalGen(nano + "#4K", nano, 0.134m, nanoStatement);

        yield return FalGen("blackforestlabs/flux-3/text-to-image", "blackforestlabs/flux-3/text-to-image", 0.048m,
            "\"Image pricing varies by resolution, starting at $0.0205 per image. A 1K (1 MP) image costs $0.024. " +
            "Note: these promotional launch rates are 50% off for a limited time. The discount ends October 8, " +
            "after which a 1K (1 MP) image will cost $0.048\" - post-discount figure, as for the edit endpoint; " +
            "the client forwards only the `1k` tier");
        yield return FalGen("bytedance/seedream/v5/pro/text-to-image", "bytedance/seedream/v5/pro/text-to-image", 0.0675m,
            "the page sentence reads \"$0.03375 per generated layer\" under 1536x1536 and $0.0675 over it, while " +
            "the gallery listing reads \"Tentative pricing is $0.0675 per image\" up to 1536x1536 and $0.135 above; " +
            "the two disagree by 2x, so the HIGHER ($0.0675) is the card. The client forwards only image_size=auto_1K");

        string transparent = "fal-ai/ideogram/v3/generate-transparent";
        string transparentStatement = "\"Your request will cost $0.03 with TURBO, $0.06 with BALANCED, and $0.09 with " +
                                      "QUALITY.\"; keyed on the `rendering_speed` tier. An OLDER generation, kept " +
                                      "because ideogram/v4.5/generate-transparent does not exist (fal answers 404)";
        yield return FalGen(transparent + "#TURBO", transparent, 0.03m, transparentStatement);
        yield return FalGen(transparent + "#BALANCED", transparent, 0.06m, transparentStatement + " (the endpoint's default)");
        yield return FalGen(transparent + "#QUALITY", transparent, 0.09m, transparentStatement);
    }
}
