using AutoPrint.Models;

namespace AutoPrint.Services;

/// <summary>Жёстко зашитые данные для кнопок "Системный тест" и "Эмуляция веб-запроса".</summary>
public static class TestData
{
    /// <summary>Тестовый чек как внутренняя модель (заголовок, товары, итог, штрих-код).</summary>
    public static ReceiptModel SystemTestReceipt() => new()
    {
        Header = "ООО «МУЛЕКС ТЕСТ»",
        SubHeader = "г. Москва, ул. Тестовая, 1",
        Items = new()
        {
            new ReceiptItem { Name = "Кофе Американо",        Qty = 2, Price = 150.00m },
            new ReceiptItem { Name = "Круассан миндальный",   Qty = 1, Price = 220.50m },
            new ReceiptItem { Name = "Вода 0.5л",             Qty = 3, Price = 60.00m  },
        },
        Total = 2 * 150.00m + 220.50m + 3 * 60.00m,   // 700.50
        Barcode = "2000000012345",
        Footer = "Спасибо за покупку!\nAutoPrint • Mulex P80"
    };

    /// <summary>Системный тестовый чек, сериализованный в JSON (для очереди/конвейера).</summary>
    public static string SystemTestJson() =>
        System.Text.Json.JsonSerializer.Serialize(SystemTestReceipt(),
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    /// <summary>Тот же чек в виде JSON — имитация тела веб-запроса из браузера.</summary>
    public static string SampleJsonPayload() => """
    {
      "header": "ИНТЕРНЕТ-МАГАЗИН №42",
      "subHeader": "Заказ №100500 от 10.07.2026",
      "items": [
        { "name": "Клавиатура механическая", "qty": 1, "price": 4990.00 },
        { "name": "Коврик XL",               "qty": 1, "price": 890.00  },
        { "name": "Доставка курьером",        "qty": 1, "price": 300.00  }
      ],
      "total": 6180.00,
      "barcode": "4650000099887",
      "footer": "Оплачено онлайн. Спасибо!"
    }
    """;

    /// <summary>
    /// Фискальный чек ОФД (Казахстан) — данные точно как в «пример чека.pdf».
    /// Операционный чек: покупка USD, оплата наличными.
    /// </summary>
    public static string SampleFiscalJson() => """
    {
      "docType": "purchase",
      "orgName": "Товарищество с ограниченной ответственностью \"ECASH\"",
      "address": "г. Алматы, р-н Алмалинский, пр. Сейфулина, д.506/99",
      "bin": "221240028699",
      "znm": "KTCD1142769759317",
      "rnm": "600704388045",
      "clientBin": "951101300019",
      "date": "2026-07-09T18:18:08.030643Z",
      "cashier": "Ибраим Кадыров",
      "items": [
        { "no": 1, "name": "USD", "qty": 1, "price": 50200000000000000 }
      ],
      "payments": [
        { "method": "Қолма-қол ақша / Наличные", "amount": 50200000000000000 }
      ],
      "total": 50200000000000000,
      "kkmCode": "600704388045",
      "checkUrl": "https://consumer.oofd.kz"
    }
    """;

    /// <summary>Примеры служебных чеков (смена, касса) — по одному на каждый тип.</summary>
    public static string SampleShiftOpenJson() => """
    {
      "docType": "shift_open",
      "orgName": "Товарищество с ограниченной ответственностью \"ECASH\"",
      "address": "г. Алматы, р-н Алмалинский, пр. Сейфулина, д.506/99",
      "bin": "221240028699", "znm": "KTCD1142769759317", "rnm": "600704388045",
      "date": "2026-07-27T09:00:00Z", "cashier": "Ибраим Кадыров",
      "shiftNumber": "42", "cashInDrawer": 15000,
      "kkmCode": "600704388045", "checkUrl": "https://consumer.oofd.kz"
    }
    """;

    public static string SampleShiftCloseJson() => """
    {
      "docType": "shift_close",
      "orgName": "Товарищество с ограниченной ответственностью \"ECASH\"",
      "address": "г. Алматы, р-н Алмалинский, пр. Сейфулина, д.506/99",
      "bin": "221240028699", "znm": "KTCD1142769759317", "rnm": "600704388045",
      "date": "2026-07-27T21:00:00Z", "cashier": "Ибраим Кадыров",
      "shiftNumber": "42",
      "counters": [
        { "label": "Кол-во чеков продаж", "value": "37" },
        { "label": "Сумма продаж", "value": "184 500" },
        { "label": "Кол-во чеков покупок", "value": "4" },
        { "label": "Сумма покупок", "value": "12 300" }
      ],
      "cashInDrawer": 187200,
      "kkmCode": "600704388045", "checkUrl": "https://consumer.oofd.kz"
    }
    """;

    public static string SampleDepositJson() => """
    {
      "docType": "deposit",
      "orgName": "Товарищество с ограниченной ответственностью \"ECASH\"",
      "address": "г. Алматы, р-н Алмалинский, пр. Сейфулина, д.506/99",
      "bin": "221240028699", "znm": "KTCD1142769759317", "rnm": "600704388045",
      "date": "2026-07-27T12:30:00Z", "cashier": "Ибраим Кадыров",
      "amount": 50000, "cashInDrawer": 237200,
      "kkmCode": "600704388045", "checkUrl": "https://consumer.oofd.kz"
    }
    """;

    public static string SampleWithdrawalJson() => """
    {
      "docType": "withdrawal",
      "orgName": "Товарищество с ограниченной ответственностью \"ECASH\"",
      "address": "г. Алматы, р-н Алмалинский, пр. Сейфулина, д.506/99",
      "bin": "221240028699", "znm": "KTCD1142769759317", "rnm": "600704388045",
      "date": "2026-07-27T18:45:00Z", "cashier": "Ибраим Кадыров",
      "amount": 30000, "cashInDrawer": 207200,
      "kkmCode": "600704388045", "checkUrl": "https://consumer.oofd.kz"
    }
    """;

    /// <summary>Пример HTML-чека (для формата HTML).</summary>
    public static string SampleHtmlPayload() => """
    <div style="text-align:center">
      <h2 style="margin:0">КОФЕЙНЯ «ПАР»</h2>
      <div>Смена №7 • Касса 2</div>
    </div>
    <hr>
    <table style="width:100%; border-collapse:collapse">
      <tr><td>Латте 0.3</td><td style="text-align:right">210,00</td></tr>
      <tr><td>Чизкейк</td><td style="text-align:right">320,00</td></tr>
    </table>
    <hr>
    <div style="display:flex; justify-content:space-between; font-weight:bold; font-size:1.3em">
      <span>ИТОГО</span><span>530,00 ₽</span>
    </div>
    <p style="text-align:center">Ждём вас снова ☕</p>
    """;
}
