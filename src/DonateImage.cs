using System;

namespace NetEye
{
    /// <summary>
    /// v0.9：赞赏码以 Base64 内嵌进 exe（由 gen_donate_v09.py 从 assets/donate_200.png 生成，勿手改）。
    /// 好处：发布包永远是纯单文件，开箱就能看到赞赏码；
    /// 想换成自己的收款码，在 exe 同级放一张 donate.png 即可覆盖内嵌图。
    /// 图片本体按内容识别格式（JPEG/PNG 都行，GDI+ 不看扩展名）。
    ///
    /// v0.9 瘦身：
    ///   1) B64 从 const 改成 static readonly —— const 的字段默认值会在 #Blob
    ///      常量表里再存一份完整字符串（#US 堆里还有一份 ldstr），同一段 Base64
    ///      在 exe 里占双份；static readonly 只在 #US 堆留一份。
    ///   2) 图片本体从 53KB JPEG 换成 200x202 PNG-8（6114 字节），Base64 从 71176
    ///      字符降到 8152 字符。两步合计 exe 从 467,456 B 瘦到 196,096 B（-58%）。
    /// </summary>
    internal static class DonateImage
    {
        /// <summary>测试钩子：true 时视为「没有内嵌图」（用于验证缺图提示路径），生产恒 false</summary>
        internal static bool DisabledForTest = false;

        // v0.9：static readonly（不是 const）—— const 会在 #Blob 里再存一份完整字符串
        private static readonly string B64 =
            "iVBORw0KGgoAAAANSUhEUgAAAMgAAADKCAMAAADXTv+nAAAAYFBMVEX///////z//Pn+//7+/v/+/v79/v7+/vz9/f39/fv9+fX8/fz7+/v69/T39/Ts8u7r49nu07XY1M6qya7AtqmEs42unZCOioRhWlckJCQKCgoEBAQDAwMCAgIBAQEAAABJbr5RAAAXPUlEQVR42u2dC2OquBaFgxgBiYTmMCjv//8v71o7PNRTK/bU9sxcc2fmYrWWz2S/d6JSr/G3jWHVuP3SD97q7Ifr//6n7+8F8gJ5gawBufeHbj/17ks/AH3/Hv/0g3gCyPU9vkBeIP9ZkOE2yDC/yfyq+cXv3+P87Nnrb92z/PNHIPc+kXtK8p7SXKWpr99v9f29QF4gL5CvBXlfvd7zldaHQi+QF8gL5OdBHg137n0Q79IO9576q0HUC+QF8q8AuRtYXWup23/4thb7IAa7FcR8eYR4Tz1+CmR4gbxAPgei/wMgPYfRMf4nT2n9fJD1mvSe" +
            "LyRXsXDEGqPuu66v5Ydd3w9DvOb374J+QRL7Hoh86oZXXcrLmpe1wo9Nx0sjc/MvAMHda5XxqjFcTBVnpFJYXabBShvsDPqXg2S8spCPockEhJcVZyRr+HzO57O/G6Tvu7a1JssyN3T9UAMkVqca42Qxcl7VDk8b2zRt130tyKN105t+BhRUakxWye1iPQ21xYKKiWCLuqrqKuelgFWAMem9939uAfjdx154RY4ruexmEHm+EImXVXfiZbXq/X8AJMWEDF2mU6OrvmlbKFroXKtgRNLYmLjomqarLS9PvKx0St3894Fk4BhaS5BT33QcLWZEG05JnGJGWnkcG8xI0wwnlWoZfxuI11JYSnKjYtF7v7RSxbv3IBmZTkNLEKrma5SvAPkg6LiT6uibBtJLd6SpThjHYholH56cSglC8y6Pq6ETEK3sqeLAbzfmgYLvbVW6Ov3yQYWpp9mTq2J+T1P7x0ZmhHrMv3QQkFjly6/X60HuRLB/ANJTsPsed9pgctoi" +
            "NWkKPWxSW7eQa4CkHgSPMLoFBM/DmnT41b8BhEYQgxDgaYYipYnQEHJbtQSbZ8SDtMMCMvT+HYZK658E6WWhnC8wLH+/tOIYIM24tLzW6obrpTWBtB/ZlcdA7uVF3wPRaS9u1GketSikI0YB+6HM0Qu7gHTtLOz9FcgAEOdym93L1Hws7PeqMR9/TN3glqeqoRkl/qTO7Z3XWkOtvWVvr2ek95PV6DWB1s37+1OQIs5SDpNmBIH8Qli8wZMfg2gCsSktO00KnheQbhGzpq/jHwRpKQMahtDQX4Qt7OCg1O0Jt5ymv4GIQWxrPD9rrbYnCH+rqw0nsf9JED0trbafltYytPJay1t67zSeKDj5JPyjhfE6uHsWyPAhSCNaSTsx5RVXzbV5z5V3UWRGcPuiAY6cEeuFH65B7zWGc9ZmTb863LgJ8qg5n0CMOorYwlMc1W+5vGJSv+PS8m491XPsL08DHJwTY8m84fRkdxbDzV6WPwNpR5C+hpx3fVcPhTZGl33b" +
            "ig/c9LNlp/erYz8MdVoap/SWxRvWmdEO4VjrY/pvBenFUewLnWX6ODQt7h0C23sQ6C9gCQgfjzMCAFIYTAz8+xiabgYxGUBo5L8fRFbTNCNUvN241LBmykmAubREa2HRMYaf7kHiFP7/tLTg/xKkRXJCfxcIcwy06Z1E50UGCT0OXEnL4xKfbX/xmCASs8vIMB86lSs/I3yRB3EE0X8Gsi4rgUApq2TNFw7DBxUDDdrZY1rr2i2PGfs21Ty8+vXhSIf1Wctly99ySq/tQv1Y/a5KrzhZ8/mcTBgktD173A6ipZbHmKAzvepBujlDPEsdl2r8bSCxdhK6ujjLkEyoIeV+Bvzjho/hB8/JBh92tLTjVAhdAxcGgkXL3lApIF2xhCqFivV3gehRnbolRqfXBRCTpXg8fryYEc0YHTeIG6U31U86289I7X9MCmi4FoFZy/gl/b4ZWUDkRr3j109Lqz0DieVxt6i5c5D2zPmcl175WZC7LReX6cRW8qBXM+Jj9lYc" +
            "jdrnUHxei6Digoi74p8a4xFkumc/xh17CJF/Uc73R5TcP1QVXgFy/UZyd+UlCFb9IrKzlRlBfB6Lya8pkBrTQWd/tOCL5jBx8iefDmKuQLiY6neGZSbohGxRI269nZ9oOCNafH9a9yyFJwB30/JhTBAnn8pzQfLRBWkXGemHU+Hy3wc+9VhbXlkGjOnyDHKOfkbE+crgm3H9TZYfIMOzQXoaLLogzdmMNMdbanqOd/Xk7E6KT48gWnwVmeHKXoL0XwtyJexDldNcQ6fOMzJUBqskin8b9DWYxY4l0Zvyanpmqo9SQTsIO/REdZpmxIwg+hMgq1q4fYZUVvnQn4EcYwS0ajvamK0Ow1CFMheaPybF+eRomQ49+TvqCJkxFvWfSf7pZPZru1Q/A9Iv7gQyi/PS8tFuFOpQbjEMt9ut3obySOlIbSMVx1cJ6xlEXGPv/dKk6znr8mQQ8WphMvpBQIyAOCrTKIoSZhrCVEBCDnIE8Q64odHxu60DeA4qCvGIX4sC" +
            "+3SQTuweTQZDJg+iBQQAUeQ/6ChQKtztdpwafuIR/lGZmcX7HZBhmEB8NuzZIPrKWwWI0uOM6DBJbO5sGOtAhztjkiiIwq0KI4BFyE1kqY7fmRNmJAlyBjlmXd6RkjUgd/IQoxDiVl0lllq8CZ8FYQiBu1Q5fwTFlCAUj1Sw2SQRpmkfHGxZ2AJyFL9TbdPUWkN3lnctaEVFHTdZ/MkWj3v54jHrMbkccTW6gFhaEm7pvFBhApTIOrcPNgaTQY+qzJQ75lGSRLEn0RceqBv9ZT+OFLxS4h2tngWCu0XKs0GMjdpByrJB20BYCEJpSIo8y3a7ODqUx2PpguCAinvp9GYf5a7IA5N4IaDwqMWxYnDDegPzpnVbLiDps0C0gCAU706xgHQ+HhKQLe6oxO2GaRrkxxIoiHxlBR4wFxDpEhOkRa9BMy9/FM90UloRb74ZSkZn3nMw6rlLa0qJptX4JEG22yS0Ls+CaGcAIiQus0UQ4GdpglxkWeTWMDOHjPCZ2Iuw" +
            "Dz4aYYRY+gQfPYf4MZBVgZU36cganI5cNpL3lEoIH9BH2m5j6KcgsbjNyMoTVoVZkRugpWmSWyWRh0rTUEUmwqyEo2G0x1LCTEwIqnBTxRRQcJP7YfWmlWElyDTKJSWaX+qfKE0yLKYgw61hRnDPCVRZHsVhuItUeMBlDpJ9ZrNNFIVBEKhQeUVWDGMc3F8Fk8NTQFgGbMs0Q/zQ1oivHaMMxBSTHxslEO4CWYXC2cM+oIFTSENCKBDzYTaiJNgUXHNH2JswjEaQVOIRsU4sqkqhcawAPQlEdK3XKh5EMjdnziDuuwREYVUUYAn5vgeR7tDkNoxSY4+yIDFbu1QHowIeQbrWqy7mI54MQlcRIBlSpHxEi37hC8ZbyHtS0HWJ091ucnu9+6jiHbLZWHOYkzyEBxYF27NQ97dQuX9waa0U9h6FpmboxxnxRj2/SkHRq1dhUNjIaHhb3uWFmgLIlq4WHDBHkGO52SfgDCK6x8qWEHYm+BbzXkmnBJoi2n74xNaU" +
            "O2lfdDJ0AjLNQarTGWT0dRODCYGjtaWzuNttdbzbxls/bxCVZFNSMRcODkwaBUHoLXtPkzhU2ayRfSidrWnHvWNHboFQz8NpQu9SzIhvWVYhdZBO94j3gk3ChQPNlCSIsBI6+Pg3JEiQUdbLstjvE3CAPqZBFG+6smJi0lQMYjtm758Dgs+tpHXmn4vN1FbGAWUKH5H6NtObBH58sofKheObqQDaKoA3HMJDToK9K+C3lMRNonFGWJOoewGR7IqSZMQzZ6QZLe/kei0ighnBp565I7zXKDY7+iTO0iN2hyCwGSclZJkX7JtD+Va6ELx+RgZv9yozB43l0nH3tBk52iXdc7a0aBCjPRKFBcw4nSlbOKGQ/xblBuVqKACGkQbWpHgrDxtMiPYgvqwgKaVMCkPSA/kYyL08ljpvW6IumUdF9btMSQiRAIeLU4RYWU6B3h8O9nDIEXBBJYOAIJFJXHEsciwurEDtfS0WeqyTvk7KoMqkBhSv7y55FKRdihyS18Lf" +
            "HCclDACyQQxlkn2Auywc/V9gAAYjL10WBDIjIVzkonx7K/cQfq0SHyEiqe0mZSLxin6oTebxGRl8xM78Q+00RdPHfTDbyQYguUJMWBRYf1RPOVYWiueH/d4yPx0yaAyKfH8gCMwQAn0P4qu68Od7AUEmbFq2TwIZm+VaKSM4SeCMpj1JOSGlDfewJIfcvWG9HwByKvYHyHxOlNAgfgwKt4cwvdETHrXWPCOtn5EpeH86CM08m6rZdG1iCeTCaJcE/NSDJC/2mBD39pYfSsjvPrfueDyVIIGvCDsP8eHaAtduBkltZhzfs11AHlxa6zIQI4j35RCzL3XOwkt8GCIM4d3BJDqIuM9VQ12V6OLABCFQxDKzwEbchZ++vb0Vm3S7FWHvRGv5N7VKq8cPz/oUyDAmH0w1tmhMSdy8wMp3HuRgWYw+oCMeC83mXGh2D9MCLbzfwPwTZJ/AoVySD1V6lhL6LhCWOSVm70YQCP14e/jAD3NBPYegkwZPAGxPuYBai1zp" +
            "QaT3kSViuPAdLDsmbApungiSXlScoOxZXWczpiytyIwgm4PMAefjME6Mt6H4gaOHb4I9XEe8soBTJjIirZ1tZdmgKgTS7nUO82yQuRmT6jeUzxlaFfGt2A4WIKC6CEMk/Acg+33gRETwUjhcHsQvrfjzB8y903l9O3B5D6TrEfFJ+4gI8VsB62FxQa+kpKDjH0zQIae4WA9SeAyS2ECW1lAd5yG1CidBScXkw7sf73vzcFvdXj+OoX4ljjsDkWQBFj689SCjrIPlSC8LZn28W2QaS+elBvmH4IyDpiQe7QhGfp5pvLPx/6anuAqkIUh3CYIsUcvYHSCuGBcMnb/yLZ/v9w3zQg548Abr6o0JiNIzRlvvazGj4eq2njKN6BwYe9SfBdJdgUDd9AISHcaF/7bJHf2peeCGc5oR/McqEL4x/3CSV5cHNdZHfOMZopApZdo/b0Z0M0irxuWM9L1kGuMgH0FyKNmIXsoy4KBQXMo8CGjST8zqeRCrfH0EYfPYr/Ut" +
            "IKwfNv0lCFIEko1X08qCREAQfLmaVoQgTjPngLS25f0zJXQqFpBxMxnd+KpeQHwadTXIvVTKWb+IQZiQL53UBGmHo3MMgOidTwKx2QRuac/IDxvMTykOsFe8AnL00wfvN3OuvHBRxmx8Y/TqUwMfAvG7U8f6yGIQvUNBj3FURawfBBtEIlmej0AWaw1zg1kZOUYSgkTR1mupKdSd+oTfbzH/GhB4EBcguERLuYCEM8h+kxqmexKkG1C5QtEnDyJEVoe9OIvY6AMK5sBlaSVBjKxJV4uLknLDhq8hPhNEM92Abp+691uLpPcYXaa+y3JcWshiJ2mQWZBwIG+CDAvqI6iZwFoWIup+QExQy4pkKUkLBzeXseIwgrRPBJG+cegpmRFbMeAt5o6ecjJyhwh37RKkd5k3CYK9YWbEkQT3fiqZeoC5IdUmlFpPSX04JuhmkO4zIHe02Bims0RSiKVmbtqIF3KU1ClI9pMDpRKKNRqvUmRTUP6xQRBvTSROPkCPbwWM" +
            "JawL+87DGCEJykDjO03F0IKfVq/WH8L52Eb8sTy15N7HChZdFJ0EbrTWG/ha+w3zJlFIZXYsIm2iyc7AHPrSwukkLsqW7QXyTnkzuyi/NdV8OUjfo3FRRBLiwj0j6antPAjSbqNtCGzhE0BBhMvcQTeZiVOE3e8DyJE2jUPkiPWWrVt0UXwxNP0WkJzRT+oLPFz6p1Z8LRVvklxIGO3S/UVEESJFUmw4OSZJZjtDaYcOC5E0ZdiRBClzsDSMzPgvIMNzl5a9qJGbaWltY7iNuahgBOn7zYbPI08K98QVh02y30zqmWakOAQm81tldjtpo0vFVZGllabj5deCXLwd0idWjByFs5SeRRF21AyQwU5DGI2CJEzQOdr0AFNSWCjhcFlZiDmCCHv8rPuF4Sw5whTaYNxJGr/T07gaZP2pGPVSU2LrxvxzFg4QXBz8By97YKBe4ZxsaEgOxfFtBikC3Lr99c80HHJK6JgY2wiY17bDxwcG3VbKj4B0siNMaogo9UBW" +
            "vPpCx9YOC/7gymKJRGRyZAKnn8CB32yMcf+cjV+I1rkfI6PM628CaeiV9o2AcBNonC7ZX9R32PdQFGcoMjlnjxEZR1cc/5z+wfpCntQcBET/fibEE0DauUzZ5VMLps/Gs0KCSTFKUM5ZzkdZBhCPfy6Hs9k0seq7QITCZwa9Y+unJCTIZhNpkKBUlS0qoZDlVZ47x+bXxcJykBfU7LeOKsKKk/IJkEcP9uql1ZTpax+PFCrz6SCQbEhCRxGeIupSVhJCCLGyPdWZkB1R4EnthXxYrrNfUvSeNsLEqzJYa3a9qY+P96iw00tn3vv1mUbWQlHrifgvBvKJKBzOf2SLZRMw45hnsII7kZBfvwQjyz0QmkwdetP7sx093wBSs290BtEjCEpr9BPRnKl2KPvs2E63k1ZFFLNQMeSvo/xuUr+yEKjYbNbC6BzKsfPifI/Vt4BoPWcazVJ926FXVrEDWF/0ZCk2OErhPd3pbZp6neUg9IuwWHEV+/Ndb98J0p6DSCdp" +
            "/G5CnW3MKtyy5wwa2o7ScaaEKSMFtvb0XwZyT4tNB2VplhXaixmRJ4zsQbjo7Rg3JRu9le5mCNA/vw1oLc2Ed1Z3/ccKa63WWgOyVLCaqxnR7yWi5/6hLaaEvXap+w3EmnA7GtzhB0Dai6UlTQvFeMKA7wyaGkklSM+2kJIwjSOT/fqNY4fVh36icff0t4LENbrEurliJesHu96k51Gir6ljOfZnB7XYGCPnc/B1v6440q34i9N+9m8F0fVF6S2emjeRHj7NS80nJgr2J1aye5o3m2yyc5vocEbBVq88KXO1i7IuSJETNzKabjsS6DjzIeySR7Dj3sh02ZifhHwqi9KcFhE2kQEJAuZYhx+Vm4Z7R2+tjrhup4eWjz2r55Nqymk71gzS+I35iWycydTO/rJcYShvY7HtfLvXB73vq09N/ySI39QiHRcpLD0XUIG7MyU7LOsJxMwgMUCQYcA+LUS4mRcX7HLI7BXHN4NM86F9y1NWsaW6WFrCz0D8+Vr+FA50" +
            "GGE/SeplWwVwlOtK6Z8GmW14Sks/HXr0G4i4A2MSHC0nxzwzuRckVArfOQTpMyA3nfz5OzqGufh4RYu8dhvP29dMORYzr0EmOyKj4sEQFfvmT0ujUWPeLeycfR/IfKfXd/LgwcW3deDsW03787gl5BJk1GuZt3Xcv35CYzP2sXZt1123ya4+yeRzJzCvyclMJ9NIl2jXLyBKbKU/pgqywk3HeC1TpP3949Z/AsS3+XqQfjwVcDyfTU53kvK2nF0lbU32bwbhli84J4Wki6zPrEquWEDg0bRsf0eTA36Utf3fCXKxG5q+VXbekAqQdu5Lr5YDTb8M5K5LsO6och5OIR2b/nAKN28mk1FCJnQ2nstD8ZEz9DJzq9/v3onia5pWVrWl3Ot0qRanckpWTJ1L1bzFsF9lpz4onz8bBFJQUegR7PmBOqd0P1J+dDVL+F8PQiNOwTeTVur8MbmymVrVi+H4F4A0Y3Z1bhE+zx2vjn8eBLn3FQS397ff3sjfp3JGyHsH" +
            "SrJPWN07T3hYc2jn6o459QePr0a7XpM+cProD4D0/xWQ4QXygOW/J0drhPvhvWd/CDL82dHAq741aVitJB+u2n7B+eQvkBfI/zHIMHzmK5vVnfrK7Xt+4PtLPrOj5wXyAnmBrN31tqI0svb7SR54/foPRt06cucF8gJ5gXwZiFof7Qwrd6l97Ies+Uwf/+LED6Ox9TkktepbYIfV+w6Hr/qGyhfIC+SbQNZ9A+DPggx3vqL5gS8zW19+Xp0bfoG8QF4gXwai1FrH5oGEr3rsmN7bd/IHIA98LdwL5AXyAvkpEPVxeLNq4/693O0D6aPP55I/Heq+QF4g3wSivgJk+DKQ4Su+EnnVPT5Q6Fnlq30mif0CeYH8X4K8xmu8xkfjf9BVfOHyMIBcAAAAAElFTkSuQmCC";

        private static byte[] _bytes;
        private static bool _tried;

        /// <summary>内嵌图是否可用（解码成功且未被测试禁用）</summary>
        public static bool HasData
        {
            get { return !DisabledForTest && Bytes != null; }
        }

        /// <summary>内嵌图原始字节；Base64 解码失败返回 null（绝不抛异常打断主程序）</summary>
        public static byte[] Bytes
        {
            get
            {
                if (_tried) return _bytes;
                _tried = true;
                try { _bytes = Convert.FromBase64String(B64); }
                catch { _bytes = null; }
                return _bytes;
            }
        }
    }
}
