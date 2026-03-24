//using StockNotificationApi.Models;
//using System.Text.Json;

//namespace StockNotificationApi.Services.AngelOne
//{
//    public partial class AngelOneService
//    {
//        public async Task<OrderResponse> PlaceOrderAsync(OrderRequest order)
//        {
//            if (order == null) throw new ArgumentNullException(nameof(order));

//            await EnsureAuthenticatedAsync();

//            try
//            {
//                _logger.LogInformation("Placing order: {Action} {Quantity} {Symbol} @ ₹{Price:F2}",
//                    order.Action, order.Quantity, order.Symbol, order.Price);

//                var symbolToken = await _tokenCache.GetSymbolTokenAsync(order.Symbol);
//                if (string.IsNullOrEmpty(symbolToken))
//                {
//                    throw new InvalidOperationException($"No token found for symbol: {order.Symbol}");
//                }

//                var request = new
//                {
//                    variety = order.Variety ?? "NORMAL",
//                    tradingsymbol = order.Symbol,
//                    symboltoken = symbolToken,
//                    transactiontype = order.Action.ToUpperInvariant(),
//                    exchange = order.Exchange ?? "NSE",
//                    ordertype = order.OrderType ?? "LIMIT",
//                    producttype = order.ProductType ?? "DELIVERY",
//                    duration = order.Duration ?? "DAY",
//                    price = order.Price.ToString("F2"),
//                    squareoff = "0",
//                    stoploss = "0",
//                    quantity = order.Quantity.ToString()
//                };

//                return await ExecuteOrderRequestAsync(ORDER_ENDPOINT, request);
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "Error placing order for {Symbol}", order.Symbol);
//                throw;
//            }
//        }

//        public async Task<bool> CancelOrderAsync(string orderId)
//        {
//            if (string.IsNullOrWhiteSpace(orderId))
//                throw new ArgumentException("Order ID cannot be empty", nameof(orderId));

//            await EnsureAuthenticatedAsync();

//            var request = new { variety = "NORMAL", orderid = orderId };
//            var response = await ExecuteOrderRequestAsync(CANCEL_ORDER_ENDPOINT, request);

//            return response.Status == "SUCCESS";
//        }

//        public async Task<ExecutionResult> ExecuteTradeAsync(TradeAction action)
//        {
//            if (action == null) throw new ArgumentNullException(nameof(action));

//            try
//            {
//                var orderRequest = new OrderRequest
//                {
//                    Symbol = action.Symbol,
//                    Action = action.Action,
//                    Quantity = action.Quantity,
//                    Price = action.Price,
//                    Exchange = "NSE",
//                    Variety = "NORMAL",
//                    OrderType = "LIMIT",
//                    ProductType = "DELIVERY",
//                    Duration = "DAY"
//                };

//                var response = await PlaceOrderAsync(orderRequest);

//                return new ExecutionResult
//                {
//                    Success = response.Status == "SUCCESS",
//                    OrderId = response.OrderId,
//                    Symbol = action.Symbol,
//                    Quantity = action.Quantity,
//                    ExecutedPrice = action.Price,
//                    Message = response.Message,
//                    ExecutionTime = DateTime.Now
//                };
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "Error executing trade for {Symbol}", action.Symbol);
//                return new ExecutionResult
//                {
//                    Success = false,
//                    Symbol = action.Symbol,
//                    Message = ex.Message,
//                    ExecutionTime = DateTime.Now
//                };
//            }
//        }

//        private async Task<OrderResponse> ExecuteOrderRequestAsync(string endpoint, object requestBody)
//        {
//            using var client = _httpClientFactory.CreateClient();
//            client.BaseAddress = new Uri(BASE_URL);
//            client.Timeout = TimeSpan.FromSeconds(30);

//            await ConfigureClientHeadersAsync(client, true, false);

//            var response = await client.PostAsync(endpoint, CreateJsonContent(requestBody));
//            var responseContent = await response.Content.ReadAsStringAsync();

//            if (response.IsSuccessStatusCode)
//            {
//                var result = JsonSerializer.Deserialize<AngelOneOrderResponse>(responseContent,
//                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

//                if (result?.data != null)
//                {
//                    _logger.LogInformation("✅ Order {OrderId} processed successfully", result.data.orderid);

//                    return new OrderResponse
//                    {
//                        OrderId = result.data.orderid,
//                        Status = "SUCCESS",
//                        Message = "Order placed successfully",
//                        OrderTime = DateTime.Now
//                    };
//                }
//            }

//            _logger.LogError("❌ Order failed: {StatusCode} - {Error}", response.StatusCode, responseContent);

//            return new OrderResponse
//            {
//                Status = "FAILED",
//                Message = $"Order failed: {responseContent}"
//            };
//        }
//    }
//}