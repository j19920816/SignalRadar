using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using System;

namespace QuantConnect.Algorithm.CSharp.Backtest
{
    /// <summary>
    /// 固定百分比手續費模型：每筆訂單按成交金額的指定比例收取手續費。
    /// 例如 feeRate = 0.002 → 每筆收 0.2%。
    /// </summary>
    public class PercentageFeeModel : FeeModel
    {
        private readonly decimal _feeRate;

        public PercentageFeeModel(decimal feeRate)
        {
            _feeRate = feeRate;
        }

        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            var price    = parameters.Security.Price;
            var quantity = Math.Abs(parameters.Order.Quantity);
            var fee      = quantity * price * _feeRate;
            var currency = parameters.Security.QuoteCurrency.Symbol;
            return new OrderFee(new CashAmount(fee, currency));
        }
    }
}
