#region S# License
/******************************************************************************************
NOTICE!!!  This program and source code is owned and licensed by
StockSharp, LLC, www.stocksharp.com
Viewing or use of this code requires your acceptance of the license
agreement found at https://github.com/StockSharp/StockSharp/blob/master/LICENSE
Removal of this comment is a violation of the license agreement.

Project: StockSharp.Algo.Algo
File: TraderHelper.cs
Created: 2015, 11, 11, 2:32 PM

Copyright 2010 by StockSharp, LLC
*******************************************************************************************/
#endregion S# License
namespace StockSharp.Algo
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	using Ecng.Common;
	using Ecng.Collections;
	using Ecng.Compilation;
	using Ecng.Compilation.Expressions;

	using StockSharp.Algo.Storages;
	using StockSharp.BusinessEntities;
	using StockSharp.Logging;
	using StockSharp.Messages;
	using StockSharp.Localization;
	using StockSharp.Algo.Indicators;

	/// <summary>
	/// The auxiliary class for provision of various algorithmic functionalities.
	/// </summary>
	public static partial class TraderHelper
	{
		/// <summary>
		/// Check the possibility order's state change.
		/// </summary>
		/// <param name="order">Order.</param>
		/// <param name="state">Current order's state.</param>
		/// <param name="logs">Logs.</param>
		public static void ApplyNewState(this Order order, OrderStates state, ILogReceiver logs = null)
		{
			((OrderStates?)order.State).VerifyOrderState(state, order.TransactionId, logs);
			order.State = state;
		}

		/// <summary>
		/// To calculate the current price by the instrument depending on the order direction.
		/// </summary>
		/// <param name="security">The instrument used for the current price calculation.</param>
		/// <param name="provider">The market data provider.</param>
		/// <param name="direction">Order side.</param>
		/// <returns>The current price. If information in order book is insufficient, then <see langword="null" /> will be returned.</returns>
		public static Unit GetCurrentPrice(this Security security, IMarketDataProvider provider, Sides? direction = null)
		{
			if (provider == null)
				throw new ArgumentNullException(nameof(provider));

			decimal? currentPrice = null;

			if (direction != null)
			{
				currentPrice = (decimal?)provider.GetSecurityValue(security,
					direction == Sides.Buy ? Level1Fields.BestAskPrice : Level1Fields.BestBidPrice);
			}

			currentPrice ??= (decimal?)provider.GetSecurityValue(security, Level1Fields.LastTradePrice);
			currentPrice ??= 0;

			return new Unit((decimal)currentPrice).SetSecurity(security);
		}

		/// <summary>
		/// To calculate the current price by the order book depending on the order direction.
		/// </summary>
		/// <param name="depth">The order book for the current price calculation.</param>
		/// <param name="side">The order direction. If it is a buy, <see cref="MarketDepth.BestAsk2"/> value is used, otherwise <see cref="MarketDepth.BestBid2"/>.</param>
		/// <param name="priceStep"><see cref="SecurityMessage.PriceStep"/></param>
		/// <param name="priceType">The type of current price.</param>
		/// <param name="orders">Orders to be ignored.</param>
		/// <returns>The current price. If information in order book is insufficient, then <see langword="null" /> will be returned.</returns>
		/// <remarks>
		/// For correct operation of the method the order book export shall be launched.
		/// </remarks>
		public static Unit GetCurrentPrice(this IOrderBookMessage depth, Sides side, decimal? priceStep, MarketPriceTypes priceType = MarketPriceTypes.Following, IEnumerable<Order> orders = null)
		{
			if (depth == null)
				throw new ArgumentNullException(nameof(depth));

			if (orders != null)
			{
				var dict = new Dictionary<Tuple<Sides, decimal>, HashSet<Order>>();

				foreach (var order in orders)
				{
					if (!dict.SafeAdd(Tuple.Create(order.Side, order.Price)).Add(order))
						throw new InvalidOperationException(LocalizedStrings.HasDuplicates.Put(order));
				}

				var bids = depth.Bids.ToList();
				var asks = depth.Asks.ToList();

				for (var i = 0; i < bids.Count; i++)
				{
					var quote = bids[i];

					if (dict.TryGetValue(Tuple.Create(Sides.Buy, quote.Price), out var bidOrders))
					{
						foreach (var order in bidOrders)
						{
							if (!orders.Contains(order))
								quote.Volume -= order.Balance;
						}

						if (quote.Volume <= 0)
						{
							bids.RemoveAt(i);
							i--;
						}
						else
							bids[i] = quote;
					}
				}

				for (var i = 0; i < asks.Count; i++)
				{
					var quote = asks[i];

					if (dict.TryGetValue(Tuple.Create(Sides.Sell, quote.Price), out var asksOrders))
					{
						foreach (var order in asksOrders)
						{
							if (!orders.Contains(order))
								quote.Volume -= order.Balance;
						}

						if (quote.Volume <= 0)
						{
							asks.RemoveAt(i);
							i--;
						}
						else
							asks[i] = quote;
					}
				}

				depth = new QuoteChangeMessage
				{
					SecurityId = depth.SecurityId,
					ServerTime = depth.ServerTime,
					Bids = bids.ToArray(),
					Asks = asks.ToArray(),
				};
			}

			var (bid, ask) = depth.GetBestPair();
			return new MarketDepthPair(bid, ask).GetCurrentPrice(side, priceStep, priceType);
		}

		/// <summary>
		/// To calculate the current price based on the best pair of quotes, depending on the order direction.
		/// </summary>
		/// <param name="bestPair">The best pair of quotes, used for the current price calculation.</param>
		/// <param name="side">The order direction. If it is a buy, <see cref="MarketDepthPair.Ask"/> value is used, otherwise <see cref="MarketDepthPair.Bid"/>.</param>
		/// <param name="priceStep"><see cref="SecurityMessage.PriceStep"/></param>
		/// <param name="priceType">The type of current price.</param>
		/// <returns>The current price. If information in order book is insufficient, then <see langword="null" /> will be returned.</returns>
		/// <remarks>
		/// For correct operation of the method the order book export shall be launched.
		/// </remarks>
		public static Unit GetCurrentPrice(this MarketDepthPair bestPair, Sides side, decimal? priceStep, MarketPriceTypes priceType = MarketPriceTypes.Following)
		{
			if (bestPair == null)
				throw new ArgumentNullException(nameof(bestPair));

			decimal? currentPrice;

			switch (priceType)
			{
				case MarketPriceTypes.Opposite:
				{
					var quote = side == Sides.Buy ? bestPair.Ask : bestPair.Bid;
					currentPrice = quote?.Price;
					break;
				}
				case MarketPriceTypes.Following:
				{
					var quote = side == Sides.Buy ? bestPair.Bid : bestPair.Ask;
					currentPrice = quote?.Price;
					break;
				}
				case MarketPriceTypes.Middle:
				{
					if (bestPair.IsFull)
						currentPrice = bestPair.GetMiddlePrice(priceStep);
					else
						currentPrice = null;
					break;
				}
				default:
					throw new ArgumentOutOfRangeException(nameof(priceType), priceType, LocalizedStrings.InvalidValue);
			}

			return currentPrice == null
				? null
				: new Unit(currentPrice.Value);
		}

		/// <summary>
		/// To use shifting for price, depending on direction <paramref name="side" />.
		/// </summary>
		/// <param name="price">Price.</param>
		/// <param name="side">The order direction, used as shift direction (for buy the shift is added, for sell - subtracted).</param>
		/// <param name="offset">Price shift.</param>
		/// <param name="security">Security.</param>
		/// <returns>New price.</returns>
		public static decimal ApplyOffset(this Unit price, Sides side, Unit offset, Security security)
		{
			if (price == null)
				throw new ArgumentNullException(nameof(price));

			if (price.GetTypeValue == null)
				price.SetSecurity(security);

			if (offset.GetTypeValue == null)
				offset.SetSecurity(security);

			return security.ShrinkPrice((decimal)(side == Sides.Buy ? price + offset : price - offset));
		}

		/// <summary>
		/// To cut the price for the order, to make it multiple of the minimal step, also to limit number of decimal places.
		/// </summary>
		/// <param name="order">The order for which the price will be cut <see cref="Order.Price"/>.</param>
		/// <param name="rule">The price rounding rule.</param>
		public static void ShrinkPrice(this Order order, ShrinkRules rule = ShrinkRules.Auto)
		{
			if (order is null)
				throw new ArgumentNullException(nameof(order));

			order.Price = order.Security.ShrinkPrice(order.Price, rule);
		}

		/// <summary>
		/// To cut the price, to make it multiple of minimal step, also to limit number of signs after the comma.
		/// </summary>
		/// <param name="security">The instrument from which the <see cref="Security.PriceStep"/> and <see cref="Security.Decimals"/> values are taken.</param>
		/// <param name="price">The price to be made multiple.</param>
		/// <param name="rule">The price rounding rule.</param>
		/// <returns>The multiple price.</returns>
		public static decimal ShrinkPrice(this Security security, decimal price, ShrinkRules rule = ShrinkRules.Auto)
		{
			if (security is null)
				throw new ArgumentNullException(nameof(security));

			return price.ShrinkPrice(security.PriceStep, security.Decimals, rule);
		}

		/// <summary>
		/// To get the position on own trade.
		/// </summary>
		/// <param name="trade">Own trade, used for position calculation. At buy the trade volume <see cref="Trade.Volume"/> is taken with positive sign, at sell - with negative.</param>
		/// <returns>Position.</returns>
		public static decimal? GetPosition(this MyTrade trade)
		{
			if (trade == null)
				throw new ArgumentNullException(nameof(trade));

			var position = trade.Trade.Volume;

			if (trade.Order.Side == Sides.Sell)
				position *= -1;

			return position;
		}

		/// <summary>
		/// To calculate profit-loss based on the portfolio.
		/// </summary>
		/// <param name="portfolio">The portfolio, for which the profit-loss shall be calculated.</param>
		/// <returns>Profit-loss.</returns>
		public static decimal? GetPnL(this Portfolio portfolio)
		{
			if (portfolio == null)
				throw new ArgumentNullException(nameof(portfolio));

			return portfolio.CurrentValue - portfolio.BeginValue;
		}

		/// <summary>
		/// To check, whether the time is traded (has the session started, ended, is there a clearing).
		/// </summary>
		/// <param name="board">Board info.</param>
		/// <param name="time">The passed time to be checked.</param>
		/// <returns><see langword="true" />, if time is traded, otherwise, not traded.</returns>
		public static bool IsTradeTime(this ExchangeBoard board, DateTimeOffset time)
		{
			return board.ToMessage().IsTradeTime(time, out _, out _);
		}

		/// <summary>
		/// To check, whether the time is traded (has the session started, ended, is there a clearing).
		/// </summary>
		/// <param name="board">Board info.</param>
		/// <param name="time">The passed time to be checked.</param>
		/// <param name="isWorkingDay"><see langword="true" />, if the date is traded, otherwise, is not traded.</param>
		/// <param name="period">Current working time period.</param>
		/// <returns><see langword="true" />, if time is traded, otherwise, not traded.</returns>
		public static bool IsTradeTime(this ExchangeBoard board, DateTimeOffset time, out bool? isWorkingDay, out WorkingTimePeriod period)
		{
			return board.ToMessage().IsTradeTime(time, out isWorkingDay, out period);
		}

		/// <summary>
		/// To get the order direction for the position.
		/// </summary>
		/// <param name="position">The position value.</param>
		/// <returns>Order side.</returns>
		/// <remarks>
		/// A positive value equals <see cref="Sides.Buy"/>, a negative - <see cref="Sides.Sell"/>, zero - <see langword="null" />.
		/// </remarks>
		public static Sides? GetDirection(this Position position)
		{
			if (position == null)
				throw new ArgumentNullException(nameof(position));

			return position.CurrentValue?.GetDirection();
		}

		/// <summary>
		/// To get the order direction for the position.
		/// </summary>
		/// <param name="position">The position value.</param>
		/// <returns>Order side.</returns>
		/// <remarks>
		/// A positive value equals <see cref="Sides.Buy"/>, a negative - <see cref="Sides.Sell"/>, zero - <see langword="null" />.
		/// </remarks>
		public static Sides? GetDirection(this decimal position)
		{
			if (position == 0)
				return null;

			return position > 0 ? Sides.Buy : Sides.Sell;
		}

		/// <summary>
		/// Cancel orders by filter.
		/// </summary>
		/// <param name="connector">The connection of interaction with trade systems.</param>
		/// <param name="orders">The group of orders, from which the required orders shall be found and cancelled.</param>
		/// <param name="isStopOrder"><see langword="true" />, if cancel only a stop orders, <see langword="false" /> - if regular orders, <see langword="null" /> - both.</param>
		/// <param name="portfolio">Portfolio. If the value is equal to <see langword="null" />, then the portfolio does not match the orders cancel filter.</param>
		/// <param name="side">Order side. If the value is <see langword="null" />, the direction does not use.</param>
		/// <param name="board">Trading board. If the value is equal to <see langword="null" />, then the board does not match the orders cancel filter.</param>
		/// <param name="security">Instrument. If the value is equal to <see langword="null" />, then the instrument does not match the orders cancel filter.</param>
		/// <param name="securityType">Security type. If the value is <see langword="null" />, the type does not use.</param>
		public static void CancelOrders(this IConnector connector, IEnumerable<Order> orders, bool? isStopOrder = null, Portfolio portfolio = null, Sides? side = null, ExchangeBoard board = null, Security security = null, SecurityTypes? securityType = null)
		{
			if (connector == null)
				throw new ArgumentNullException(nameof(connector));

			if (orders == null)
				throw new ArgumentNullException(nameof(orders));

			orders = orders
				.Where(order => !order.State.IsFinal())
				.Where(order => isStopOrder == null || (order.Type == OrderTypes.Conditional) == isStopOrder.Value)
				.Where(order => portfolio == null || (order.Portfolio == portfolio))
				.Where(order => side == null || order.Side == side.Value)
				.Where(order => board == null || order.Security.Board == board)
				.Where(order => security == null || order.Security == security)
				.Where(order => securityType == null || order.Security.Type == securityType.Value)
				;

			orders.ForEach(connector.CancelOrder);
		}

		/// <summary>
		/// To check whether specified instrument is used now.
		/// </summary>
		/// <param name="basketSecurity">Instruments basket.</param>
		/// <param name="securityProvider">The provider of information about instruments.</param>
		/// <param name="security">The instrument that should be checked.</param>
		/// <returns><see langword="true" />, if specified instrument is used now, otherwise <see langword="false" />.</returns>
		public static bool Contains(this BasketSecurity basketSecurity, ISecurityProvider securityProvider, Security security)
		{
			if (securityProvider is null)
				return false;

			return basketSecurity.GetInnerSecurities(securityProvider).Any(innerSecurity =>
			{
				if (innerSecurity is BasketSecurity basket)
					return basket.Contains(securityProvider, security);

				return innerSecurity == security;
			});
		}

		/// <summary>
		/// Find inner security instances.
		/// </summary>
		/// <param name="security">Instruments basket.</param>
		/// <param name="securityProvider">The provider of information about instruments.</param>
		/// <returns>Instruments, from which this basket is created.</returns>
		public static IEnumerable<Security> GetInnerSecurities(this BasketSecurity security, ISecurityProvider securityProvider)
		{
			if (security == null)
				throw new ArgumentNullException(nameof(security));

			if (securityProvider == null)
				throw new ArgumentNullException(nameof(securityProvider));

			return security.InnerSecurityIds.Select(id =>
				securityProvider.LookupById(id) ?? throw new InvalidOperationException(LocalizedStrings.SecurityNoFound.Put(id))
			).ToArray();
		}

		/// <summary>
		/// To filter orders for the given instrument.
		/// </summary>
		/// <param name="orders">All orders, in which the required shall be searched for.</param>
		/// <param name="security">The instrument, for which the orders shall be filtered.</param>
		/// <returns>Filtered orders.</returns>
		public static IEnumerable<Order> Filter(this IEnumerable<Order> orders, Security security)
		{
			if (orders == null)
				throw new ArgumentNullException(nameof(orders));

			if (security == null)
				throw new ArgumentNullException(nameof(security));

			var basket = security as BasketSecurity;
			return basket?.InnerSecurityIds.SelectMany(id => orders.Where(o => o.Security.ToSecurityId() == id)) ?? orders.Where(o => o.Security == security);
		}

		/// <summary>
		/// To filter orders for the given portfolio.
		/// </summary>
		/// <param name="orders">All orders, in which the required shall be searched for.</param>
		/// <param name="portfolio">The portfolio, for which the orders shall be filtered.</param>
		/// <returns>Filtered orders.</returns>
		public static IEnumerable<Order> Filter(this IEnumerable<Order> orders, Portfolio portfolio)
		{
			if (orders == null)
				throw new ArgumentNullException(nameof(orders));

			if (portfolio == null)
				throw new ArgumentNullException(nameof(portfolio));

			return orders.Where(p => p.Portfolio == portfolio);
		}

		/// <summary>
		/// To filter orders for the given condition.
		/// </summary>
		/// <param name="orders">All orders, in which the required shall be searched for.</param>
		/// <param name="state">Order state.</param>
		/// <returns>Filtered orders.</returns>
		public static IEnumerable<Order> Filter(this IEnumerable<Order> orders, OrderStates state)
		{
			if (orders == null)
				throw new ArgumentNullException(nameof(orders));

			return orders.Where(p => p.State == state);
		}

		/// <summary>
		/// To filter orders for the given direction.
		/// </summary>
		/// <param name="orders">All orders, in which the required shall be searched for.</param>
		/// <param name="side">Order side.</param>
		/// <returns>Filtered orders.</returns>
		public static IEnumerable<Order> Filter(this IEnumerable<Order> orders, Sides side)
		{
			if (orders == null)
				throw new ArgumentNullException(nameof(orders));

			return orders.Where(p => p.Side == side);
		}

		/// <summary>
		/// To filter orders for the given instrument.
		/// </summary>
		/// <param name="trades">All trades, in which the required shall be searched for.</param>
		/// <param name="security">The instrument, for which the trades shall be filtered.</param>
		/// <returns>Filtered trades.</returns>
		[Obsolete("Use ITickTradeMessage.")]
		public static IEnumerable<Trade> Filter(this IEnumerable<Trade> trades, Security security)
		{
			if (trades == null)
				throw new ArgumentNullException(nameof(trades));

			if (security == null)
				throw new ArgumentNullException(nameof(security));

			var basket = security as BasketSecurity;
			return basket?.InnerSecurityIds.SelectMany(id => trades.Where(o => o.Security.ToSecurityId() == id)) ?? trades.Where(t => t.Security == security);
		}

		/// <summary>
		/// To filter positions for the given instrument.
		/// </summary>
		/// <param name="positions">All positions, in which the required shall be searched for.</param>
		/// <param name="security">The instrument, for which positions shall be filtered.</param>
		/// <returns>Filtered positions.</returns>
		public static IEnumerable<Position> Filter(this IEnumerable<Position> positions, Security security)
		{
			if (positions == null)
				throw new ArgumentNullException(nameof(positions));

			if (security == null)
				throw new ArgumentNullException(nameof(security));

			var basket = security as BasketSecurity;
			return basket?.InnerSecurityIds.SelectMany(id => positions.Where(o => o.Security.ToSecurityId() == id)) ?? positions.Where(p => p.Security == security);
		}

		/// <summary>
		/// To filter positions for the given portfolio.
		/// </summary>
		/// <param name="positions">All positions, in which the required shall be searched for.</param>
		/// <param name="portfolio">The portfolio, for which positions shall be filtered.</param>
		/// <returns>Filtered positions.</returns>
		public static IEnumerable<Position> Filter(this IEnumerable<Position> positions, Portfolio portfolio)
		{
			if (positions == null)
				throw new ArgumentNullException(nameof(positions));

			if (portfolio == null)
				throw new ArgumentNullException(nameof(portfolio));

			return positions.Where(p => p.Portfolio == portfolio);
		}

		/// <summary>
		/// To filter own trades for the given instrument.
		/// </summary>
		/// <param name="myTrades">All own trades, in which the required shall be looked for.</param>
		/// <param name="security">The instrument, on which the trades shall be found.</param>
		/// <returns>Filtered trades.</returns>
		public static IEnumerable<MyTrade> Filter(this IEnumerable<MyTrade> myTrades, Security security)
		{
			if (myTrades == null)
				throw new ArgumentNullException(nameof(myTrades));

			if (security == null)
				throw new ArgumentNullException(nameof(security));

			var basket = security as BasketSecurity;
			return basket?.InnerSecurityIds.SelectMany(id => myTrades.Where(t => t.Order.Security.ToSecurityId() == id)) ?? myTrades.Where(t => t.Order.Security == security);
		}

		/// <summary>
		/// To filter own trades for the given portfolio.
		/// </summary>
		/// <param name="myTrades">All own trades, in which the required shall be looked for.</param>
		/// <param name="portfolio">The portfolio, for which the trades shall be filtered.</param>
		/// <returns>Filtered trades.</returns>
		public static IEnumerable<MyTrade> Filter(this IEnumerable<MyTrade> myTrades, Portfolio portfolio)
		{
			if (myTrades == null)
				throw new ArgumentNullException(nameof(myTrades));

			if (portfolio == null)
				throw new ArgumentNullException(nameof(portfolio));

			return myTrades.Where(t => t.Order.Portfolio == portfolio);
		}

		/// <summary>
		/// To filter own trades for the given order.
		/// </summary>
		/// <param name="myTrades">All own trades, in which the required shall be looked for.</param>
		/// <param name="order">The order, for which trades shall be filtered.</param>
		/// <returns>Filtered orders.</returns>
		public static IEnumerable<MyTrade> Filter(this IEnumerable<MyTrade> myTrades, Order order)
		{
			if (myTrades == null)
				throw new ArgumentNullException(nameof(myTrades));

			if (order == null)
				throw new ArgumentNullException(nameof(order));

			return myTrades.Where(t => t.Order == order);
		}

		/// <summary>
		/// To create the search criteria <see cref="Security"/> from <see cref="SecurityLookupMessage"/>.
		/// </summary>
		/// <param name="connector">Connection to the trading system.</param>
		/// <param name="criteria">The criterion which fields will be used as a filter.</param>
		/// <param name="exchangeInfoProvider">Exchanges and trading boards provider.</param>
		/// <returns>Search criterion.</returns>
		public static Security GetSecurityCriteria(this Connector connector, SecurityLookupMessage criteria, IExchangeInfoProvider exchangeInfoProvider)
		{
			if (connector == null)
				throw new ArgumentNullException(nameof(connector));

			if (criteria == null)
				throw new ArgumentNullException(nameof(criteria));

			if (exchangeInfoProvider == null)
				throw new ArgumentNullException(nameof(exchangeInfoProvider));

			var stocksharpId = criteria.SecurityId.SecurityCode.IsEmpty() || criteria.SecurityId.BoardCode.IsEmpty()
				? string.Empty
				: connector.SecurityIdGenerator.GenerateId(criteria.SecurityId.SecurityCode, criteria.SecurityId.BoardCode);

			var secCriteria = new Security { Id = stocksharpId };
			secCriteria.ApplyChanges(criteria, exchangeInfoProvider);
			return secCriteria;
		}

		/// <summary>
		/// To filter instruments by the trading board.
		/// </summary>
		/// <param name="securities">Securities.</param>
		/// <param name="board">Trading board.</param>
		/// <returns>Instruments filtered.</returns>
		public static IEnumerable<Security> Filter(this IEnumerable<Security> securities, ExchangeBoard board)
		{
			if (securities == null)
				throw new ArgumentNullException(nameof(securities));

			if (board == null)
				throw new ArgumentNullException(nameof(board));

			return securities.Where(s => s.Board == board);
		}

		/// <summary>
		/// To filter instruments by the given criteria.
		/// </summary>
		/// <param name="securities">Securities.</param>
		/// <param name="criteria">The instrument whose fields will be used as a filter.</param>
		/// <returns>Instruments filtered.</returns>
		public static IEnumerable<Security> Filter(this IEnumerable<Security> securities, Security criteria)
		{
			return securities.Filter(criteria.ToLookupMessage());
		}

		/// <summary>
		/// To filter instruments by the given criteria.
		/// </summary>
		/// <param name="securities">Securities.</param>
		/// <param name="criteria">Message security lookup for specified criteria.</param>
		/// <returns>Instruments filtered.</returns>
		public static IEnumerable<Security> Filter(this IEnumerable<Security> securities, SecurityLookupMessage criteria)
		{
			if (securities == null)
				throw new ArgumentNullException(nameof(securities));

			if (criteria.IsLookupAll())
				return securities.TryLimitByCount(criteria).ToArray();

			var dict = securities.ToDictionary(s => s.ToMessage(), s => s);
			return dict.Keys.Filter(criteria).TryLimitByCount(criteria).Select(m => dict[m]).ToArray();
		}

		/// <summary>
		/// To get date of day T +/- of N trading days.
		/// </summary>
		/// <param name="board">Board info.</param>
		/// <param name="date">The start T date, to which are added or subtracted N trading days.</param>
		/// <param name="n">The N size. The number of trading days for the addition or subtraction.</param>
		/// <param name="checkHolidays">Whether to check the passed date for a weekday (Saturday and Sunday are days off, returned value for them is <see langword="false" />).</param>
		/// <returns>The end T +/- N date.</returns>
		public static DateTimeOffset AddOrSubtractTradingDays(this ExchangeBoard board, DateTimeOffset date, int n, bool checkHolidays = true)
			=> board.ToMessage().AddOrSubtractTradingDays(date, n, checkHolidays);

		/// <summary>
		/// To get the expiration date for <see cref="ExchangeBoard.Forts"/>.
		/// </summary>
		/// <param name="from">The start of the expiration range.</param>
		/// <param name="to">The end of the expiration range.</param>
		/// <returns>Expiration dates.</returns>
		public static IEnumerable<DateTimeOffset> GetExpiryDates(this DateTime from, DateTime to)
		{
			if (from > to)
				throw new InvalidOperationException(LocalizedStrings.StartCannotBeMoreEnd.Put(from, to));

			var board = ExchangeBoard.Forts.ToMessage();

			for (var year = from.Year; year <= to.Year; year++)
			{
				var monthFrom = year == from.Year ? from.Month : 1;
				var monthTo = year == to.Year ? to.Month : 12;

				for (var month = monthFrom; month <= monthTo; month++)
				{
					switch (month)
					{
						case 3:
						case 6:
						case 9:
						case 12:
						{
							var dt = new DateTime(year, month, 15).ApplyTimeZone(board.TimeZone);

							while (!board.IsTradeDate(dt))
							{
								dt = dt.AddDays(1);
							}
							yield return dt;
							break;
						}

						default:
							continue;
					}
				}
			}
		}

		/// <summary>
		/// To get real expiration instruments for base part of the code.
		/// </summary>
		/// <param name="baseCode">The base part of the instrument code.</param>
		/// <param name="from">The start of the expiration range.</param>
		/// <param name="to">The end of the expiration range.</param>
		/// <param name="getSecurity">The function to get instrument by the code.</param>
		/// <param name="throwIfNotExists">To generate exception, if some of instruments are not available.</param>
		/// <returns>Expiration instruments.</returns>
		public static IEnumerable<Security> GetFortsJumps(this string baseCode, DateTime from, DateTime to, Func<string, Security> getSecurity, bool throwIfNotExists = true)
		{
			if (baseCode.IsEmpty())
				throw new ArgumentNullException(nameof(baseCode));

			if (from > to)
				throw new InvalidOperationException(LocalizedStrings.StartCannotBeMoreEnd.Put(from, to));

			if (getSecurity == null)
				throw new ArgumentNullException(nameof(getSecurity));

			for (var year = from.Year; year <= to.Year; year++)
			{
				var monthFrom = year == from.Year ? from.Month : 1;
				var monthTo = year == to.Year ? to.Month : 12;

				for (var month = monthFrom; month <= monthTo; month++)
				{
					char monthCode;

					switch (month)
					{
						case 3:
							monthCode = 'H';
							break;
						case 6:
							monthCode = 'M';
							break;
						case 9:
							monthCode = 'U';
							break;
						case 12:
							monthCode = 'Z';
							break;
						default:
							continue;
					}

					var yearStr = year.To<string>();
					var code = baseCode + monthCode + yearStr.Substring(yearStr.Length - 1, 1);

					var security = getSecurity(code);

					if (security == null)
					{
						if (throwIfNotExists)
							throw new InvalidOperationException(LocalizedStrings.SecurityNoFound.Put(code));

						continue;
					}

					yield return security;
				}
			}
		}

		/// <summary>
		/// To get real expiration instruments for the continuous instrument.
		/// </summary>
		/// <param name="continuousSecurity">Continuous security.</param>
		/// <param name="provider">The provider of information about instruments.</param>
		/// <param name="baseCode">The base part of the instrument code.</param>
		/// <param name="from">The start of the expiration range.</param>
		/// <param name="to">The end of the expiration range.</param>
		/// <param name="throwIfNotExists">To generate exception, if some of instruments for passed <paramref name="continuousSecurity" /> are not available.</param>
		/// <returns>Expiration instruments.</returns>
		public static IEnumerable<Security> GetFortsJumps(this ExpirationContinuousSecurity continuousSecurity, ISecurityProvider provider, string baseCode, DateTime from, DateTime to, bool throwIfNotExists = true)
		{
			if (continuousSecurity == null)
				throw new ArgumentNullException(nameof(continuousSecurity));

			if (provider == null)
				throw new ArgumentNullException(nameof(provider));

			return baseCode.GetFortsJumps(from, to, code => provider.LookupByCode(code).FirstOrDefault(s => s.Code.EqualsIgnoreCase(code)), throwIfNotExists);
		}

		/// <summary>
		/// To fill transitions <see cref="ExpirationContinuousSecurity.ExpirationJumps"/>.
		/// </summary>
		/// <param name="continuousSecurity">Continuous security.</param>
		/// <param name="provider">The provider of information about instruments.</param>
		/// <param name="baseCode">The base part of the instrument code.</param>
		/// <param name="from">The start of the expiration range.</param>
		/// <param name="to">The end of the expiration range.</param>
		public static void FillFortsJumps(this ExpirationContinuousSecurity continuousSecurity, ISecurityProvider provider, string baseCode, DateTime from, DateTime to)
		{
			var securities = continuousSecurity.GetFortsJumps(provider, baseCode, from, to);

			foreach (var security in securities)
			{
				var expDate = security.ExpiryDate;

				if (expDate == null)
					throw new InvalidOperationException(LocalizedStrings.NoExpirationDate.Put(security.Id));

				continuousSecurity.ExpirationJumps.Add(security.ToSecurityId(), expDate.Value);
			}
		}

		/// <summary>
		/// Write order info to the log.
		/// </summary>
		/// <param name="receiver">Logs receiver.</param>
		/// <param name="order">Order.</param>
		/// <param name="operation">Order action name.</param>
		/// <param name="getAdditionalInfo">Extended order info.</param>
		public static void AddOrderInfoLog(this ILogReceiver receiver, Order order, string operation, Func<string> getAdditionalInfo = null)
		{
			receiver.AddOrderLog(LogLevels.Info, order, operation, getAdditionalInfo);
		}

		/// <summary>
		/// Write order error to the log.
		/// </summary>
		/// <param name="receiver">Logs receiver.</param>
		/// <param name="order">Order.</param>
		/// <param name="operation">Order action name.</param>
		/// <param name="getAdditionalInfo">Extended order info.</param>
		public static void AddOrderErrorLog(this ILogReceiver receiver, Order order, string operation, Func<string> getAdditionalInfo = null)
		{
			receiver.AddOrderLog(LogLevels.Error, order, operation, getAdditionalInfo);
		}

		private static void AddOrderLog(this ILogReceiver receiver, LogLevels type, Order order, string operation, Func<string> getAdditionalInfo)
		{
			if (receiver == null)
				throw new ArgumentNullException(nameof(receiver));

			if (order == null)
				throw new ArgumentNullException(nameof(order));

			var orderDescription = order.ToString();
			var additionalInfo = getAdditionalInfo == null ? string.Empty : getAdditionalInfo();

			receiver.AddLog(new LogMessage(receiver, receiver.CurrentTime, type, () => "{0}: {1} {2}".Put(operation, orderDescription, additionalInfo)));
		}

		/// <summary>
		/// Apply changes to the portfolio object.
		/// </summary>
		/// <param name="portfolio">Portfolio.</param>
		/// <param name="message">Portfolio change message.</param>
		/// <param name="exchangeInfoProvider">Exchanges and trading boards provider.</param>
		public static void ApplyChanges(this Portfolio portfolio, PositionChangeMessage message, IExchangeInfoProvider exchangeInfoProvider)
		{
			if (portfolio == null)
				throw new ArgumentNullException(nameof(portfolio));

			if (message == null)
				throw new ArgumentNullException(nameof(message));

			if (exchangeInfoProvider == null)
				throw new ArgumentNullException(nameof(exchangeInfoProvider));

			if (!message.BoardCode.IsEmpty())
				portfolio.Board = exchangeInfoProvider.GetOrCreateBoard(message.BoardCode);

			if (!message.ClientCode.IsEmpty())
				portfolio.ClientCode = message.ClientCode;

			ApplyChanges(portfolio, message);
		}

		/// <summary>
		/// Apply changes to the position object.
		/// </summary>
		/// <param name="position">Position.</param>
		/// <param name="message">Position change message.</param>
		public static void ApplyChanges(this Position position, PositionChangeMessage message)
		{
			if (position == null)
				throw new ArgumentNullException(nameof(position));

			if (message == null)
				throw new ArgumentNullException(nameof(message));

			var pf = position as Portfolio ?? position.Portfolio;

			foreach (var change in message.Changes)
			{
				try
				{
					switch (change.Key)
					{
						case PositionChangeTypes.BeginValue:
							position.BeginValue = (decimal)change.Value;
							break;
						case PositionChangeTypes.CurrentValue:
							position.CurrentValue = (decimal)change.Value;
							break;
						case PositionChangeTypes.BlockedValue:
							position.BlockedValue = (decimal)change.Value;
							break;
						case PositionChangeTypes.CurrentPrice:
							position.CurrentPrice = (decimal)change.Value;
							break;
						case PositionChangeTypes.AveragePrice:
							position.AveragePrice = (decimal)change.Value;
							break;
						case PositionChangeTypes.RealizedPnL:
							position.RealizedPnL = (decimal)change.Value;
							break;
						case PositionChangeTypes.UnrealizedPnL:
							position.UnrealizedPnL = (decimal)change.Value;
							break;
						case PositionChangeTypes.Commission:
							position.Commission = (decimal)change.Value;
							break;
						case PositionChangeTypes.VariationMargin:
							position.VariationMargin = (decimal)change.Value;
							break;
						case PositionChangeTypes.Currency:
							position.Currency = (CurrencyTypes)change.Value;
							break;
						case PositionChangeTypes.ExpirationDate:
							position.ExpirationDate = (DateTimeOffset)change.Value;
							break;
						case PositionChangeTypes.SettlementPrice:
							position.SettlementPrice = (decimal)change.Value;
							break;
						case PositionChangeTypes.Leverage:
							position.Leverage = (decimal)change.Value;
							break;
						case PositionChangeTypes.State:
							if (pf != null)
								pf.State = (PortfolioStates)change.Value;
							break;
						case PositionChangeTypes.CommissionMaker:
							position.CommissionMaker = (decimal)change.Value;
							break;
						case PositionChangeTypes.CommissionTaker:
							position.CommissionTaker = (decimal)change.Value;
							break;
						case PositionChangeTypes.BuyOrdersCount:
							position.BuyOrdersCount = (int)change.Value;
							break;
						case PositionChangeTypes.SellOrdersCount:
							position.SellOrdersCount = (int)change.Value;
							break;
						case PositionChangeTypes.BuyOrdersMargin:
							position.BuyOrdersMargin = (decimal)change.Value;
							break;
						case PositionChangeTypes.SellOrdersMargin:
							position.SellOrdersMargin = (decimal)change.Value;
							break;
						case PositionChangeTypes.OrdersMargin:
							position.OrdersMargin = (decimal)change.Value;
							break;
						case PositionChangeTypes.OrdersCount:
							position.OrdersCount = (int)change.Value;
							break;
						case PositionChangeTypes.TradesCount:
							position.TradesCount = (int)change.Value;
							break;
						case PositionChangeTypes.LiquidationPrice:
							position.LiquidationPrice = (decimal)change.Value;
							break;

						// skip unknown fields
						//default:
						//	throw new ArgumentOutOfRangeException(nameof(change), change.Key, LocalizedStrings.InvalidValue);
					}
				}
				catch (Exception ex)
				{
					throw new InvalidOperationException(LocalizedStrings.CommandNotProcessedReason.Put(nameof(PositionChangeMessage), change.Key), ex);
				}
			}

			position.LocalTime = message.LocalTime;
			position.LastChangeTime = message.ServerTime;
		}

		/// <summary>
		/// Apply change to the security object.
		/// </summary>
		/// <param name="security">Security.</param>
		/// <param name="changes">Changes.</param>
		/// <param name="serverTime">Change server time.</param>
		/// <param name="localTime">Local timestamp when a message was received/created.</param>
		/// <param name="defaultHandler">Default handler.</param>
		public static void ApplyChanges(this Security security, IEnumerable<KeyValuePair<Level1Fields, object>> changes, DateTimeOffset serverTime, DateTimeOffset localTime, Action<Security, Level1Fields, object> defaultHandler = null)
		{
			if (security == null)
				throw new ArgumentNullException(nameof(security));

			if (changes == null)
				throw new ArgumentNullException(nameof(changes));

			var bidChanged = false;
			var askChanged = false;
			var lastTradeChanged = false;
			var bestBid = security.BestBid ?? new QuoteChange();
			var bestAsk = security.BestAsk ?? new QuoteChange();

			var lastTrade = new ExecutionMessage
			{
				DataTypeEx = DataType.Ticks,
			};

			var lastTick = security.LastTick;

			if (lastTick is not null)
			{
				lastTrade.TradePrice = lastTick.Price;
				lastTrade.TradeVolume = lastTick.Volume;
			}

			foreach (var pair in changes)
			{
				var value = pair.Value;

				try
				{
					switch (pair.Key)
					{
						case Level1Fields.OpenPrice:
							security.OpenPrice = (decimal)value;
							break;
						case Level1Fields.HighPrice:
							security.HighPrice = (decimal)value;
							break;
						case Level1Fields.LowPrice:
							security.LowPrice = (decimal)value;
							break;
						case Level1Fields.ClosePrice:
							security.ClosePrice = (decimal)value;
							break;
						case Level1Fields.StepPrice:
							security.StepPrice = (decimal)value;
							break;
						case Level1Fields.PriceStep:
							security.PriceStep = (decimal)value;
							break;
						case Level1Fields.Decimals:
							security.Decimals = (int)value;
							break;
						case Level1Fields.VolumeStep:
							security.VolumeStep = (decimal)value;
							break;
						case Level1Fields.Multiplier:
							security.Multiplier = (decimal)value;
							break;
						case Level1Fields.BestBidPrice:
							bestBid.Price = (decimal)value;
							bidChanged = true;
							break;
						case Level1Fields.BestBidVolume:
							bestBid.Volume = (decimal)value;
							bidChanged = true;
							break;
						case Level1Fields.BestAskPrice:
							bestAsk.Price = (decimal)value;
							askChanged = true;
							break;
						case Level1Fields.BestAskVolume:
							bestAsk.Volume = (decimal)value;
							askChanged = true;
							break;
						case Level1Fields.ImpliedVolatility:
							security.ImpliedVolatility = (decimal)value;
							break;
						case Level1Fields.HistoricalVolatility:
							security.HistoricalVolatility = (decimal)value;
							break;
						case Level1Fields.TheorPrice:
							security.TheorPrice = (decimal)value;
							break;
						case Level1Fields.Delta:
							security.Delta = (decimal)value;
							break;
						case Level1Fields.Gamma:
							security.Gamma = (decimal)value;
							break;
						case Level1Fields.Vega:
							security.Vega = (decimal)value;
							break;
						case Level1Fields.Theta:
							security.Theta = (decimal)value;
							break;
						case Level1Fields.Rho:
							security.Rho = (decimal)value;
							break;
						case Level1Fields.MarginBuy:
							security.MarginBuy = (decimal)value;
							break;
						case Level1Fields.MarginSell:
							security.MarginSell = (decimal)value;
							break;
						case Level1Fields.OpenInterest:
							security.OpenInterest = (decimal)value;
							break;
						case Level1Fields.MinPrice:
							security.MinPrice = (decimal)value;
							break;
						case Level1Fields.MaxPrice:
							security.MaxPrice = (decimal)value;
							break;
						case Level1Fields.BidsCount:
							security.BidsCount = (int)value;
							break;
						case Level1Fields.BidsVolume:
							security.BidsVolume = (decimal)value;
							break;
						case Level1Fields.AsksCount:
							security.AsksCount = (int)value;
							break;
						case Level1Fields.AsksVolume:
							security.AsksVolume = (decimal)value;
							break;
						case Level1Fields.State:
							security.State = (SecurityStates)value;
							break;
						case Level1Fields.LastTradePrice:
							lastTrade.TradePrice = (decimal)value;
							lastTradeChanged = true;
							break;
						case Level1Fields.LastTradeVolume:
							lastTrade.TradeVolume = (decimal)value;
							lastTradeChanged = true;
							break;
						case Level1Fields.LastTradeId:
							lastTrade.TradeId = (long)value;
							lastTradeChanged = true;
							break;
						case Level1Fields.LastTradeStringId:
							lastTrade.TradeStringId = (string)value;
							lastTradeChanged = true;
							break;
						case Level1Fields.LastTradeTime:
							lastTrade.ServerTime = (DateTimeOffset)value;
							lastTradeChanged = true;
							break;
						case Level1Fields.LastTradeUpDown:
							lastTrade.IsUpTick = (bool)value;
							lastTradeChanged = true;
							break;
						case Level1Fields.LastTradeOrigin:
							lastTrade.OriginSide = (Sides)value;
							lastTradeChanged = true;
							break;
						case Level1Fields.IsSystem:
							lastTrade.IsSystem = (bool)value;
							lastTradeChanged = true;
							break;
						case Level1Fields.TradesCount:
							security.TradesCount = (int)value;
							break;
						case Level1Fields.HighBidPrice:
							security.HighBidPrice = (decimal)value;
							break;
						case Level1Fields.LowAskPrice:
							security.LowAskPrice = (decimal)value;
							break;
						case Level1Fields.Yield:
							security.Yield = (decimal)value;
							break;
						case Level1Fields.VWAP:
							security.VWAP = (decimal)value;
							break;
						case Level1Fields.SettlementPrice:
							security.SettlementPrice = (decimal)value;
							break;
						case Level1Fields.AveragePrice:
							security.AveragePrice = (decimal)value;
							break;
						case Level1Fields.Volume:
							security.Volume = (decimal)value;
							break;
						case Level1Fields.Turnover:
							security.Turnover = (decimal)value;
							break;
						case Level1Fields.BuyBackPrice:
							security.BuyBackPrice = (decimal)value;
							break;
						case Level1Fields.BuyBackDate:
							security.BuyBackDate = (DateTimeOffset)value;
							break;
						case Level1Fields.CommissionTaker:
							security.CommissionTaker = (decimal)value;
							break;
						case Level1Fields.CommissionMaker:
							security.CommissionMaker = (decimal)value;
							break;
						case Level1Fields.MinVolume:
							security.MinVolume = (decimal)value;
							break;
						case Level1Fields.MaxVolume:
							security.MaxVolume = (decimal)value;
							break;
						case Level1Fields.UnderlyingMinVolume:
							security.UnderlyingSecurityMinVolume = (decimal)value;
							break;
						case Level1Fields.IssueSize:
							security.IssueSize = (decimal)value;
							break;
						default:
						{
							defaultHandler?.Invoke(security, pair.Key, pair.Value);
							break;
							//throw new ArgumentOutOfRangeException();
						}
					}
				}
				catch (Exception ex)
				{
					throw new InvalidOperationException(LocalizedStrings.CommandNotProcessedReason.Put(nameof(Level1ChangeMessage), pair.Key), ex);
				}
			}

			if (bidChanged)
				security.BestBid = bestBid;

			if (askChanged)
				security.BestAsk = bestAsk;

			if (lastTradeChanged)
			{
				if (lastTrade.ServerTime == default)
					lastTrade.ServerTime = serverTime;

				lastTrade.LocalTime = localTime;

				security.LastTick = lastTrade;
			}

			security.LocalTime = localTime;
			security.LastChangeTime = serverTime;
		}

		/// <summary>
		/// Apply change to the security object.
		/// </summary>
		/// <param name="security">Security.</param>
		/// <param name="message">Changes.</param>
		public static void ApplyChanges(this Security security, Level1ChangeMessage message)
		{
			if (security == null)
				throw new ArgumentNullException(nameof(security));

			if (message == null)
				throw new ArgumentNullException(nameof(message));

			security.ApplyChanges(message.Changes, message.ServerTime, message.LocalTime);
		}

		/// <summary>
		/// Apply change to the security object.
		/// </summary>
		/// <param name="security">Security.</param>
		/// <param name="message">Meta info.</param>
		/// <param name="exchangeInfoProvider">Exchanges and trading boards provider.</param>
		/// <param name="isOverride">Override previous security data by new values.</param>
		public static void ApplyChanges(this Security security, SecurityMessage message, IExchangeInfoProvider exchangeInfoProvider, bool isOverride = true)
		{
			if (security == null)
				throw new ArgumentNullException(nameof(security));

			if (message == null)
				throw new ArgumentNullException(nameof(message));

			if (exchangeInfoProvider == null)
				throw new ArgumentNullException(nameof(exchangeInfoProvider));

			var secId = message.SecurityId;

			if (!secId.SecurityCode.IsEmpty())
			{
				if (isOverride || security.Code.IsEmpty())
					security.Code = secId.SecurityCode;
			}

			if (!secId.BoardCode.IsEmpty())
			{
				if (isOverride || security.Board == null)
					security.Board = exchangeInfoProvider.GetOrCreateBoard(secId.BoardCode);
			}

			if (message.Currency != null)
			{
				if (isOverride || security.Currency == null)
					security.Currency = message.Currency;
			}

			if (message.ExpiryDate != null)
			{
				if (isOverride || security.ExpiryDate == null)
					security.ExpiryDate = message.ExpiryDate;
			}

			if (message.VolumeStep != null)
			{
				if (isOverride || security.VolumeStep == null)
					security.VolumeStep = message.VolumeStep.Value;
			}

			if (message.MinVolume != null)
			{
				if (isOverride || security.MinVolume == null)
					security.MinVolume = message.MinVolume.Value;
			}

			if (message.MaxVolume != null)
			{
				if (isOverride || security.MaxVolume == null)
					security.MaxVolume = message.MaxVolume.Value;
			}

			if (message.Multiplier != null)
			{
				if (isOverride || security.Multiplier == null)
					security.Multiplier = message.Multiplier.Value;
			}

			if (message.PriceStep != null)
			{
				if (isOverride || security.PriceStep == null)
					security.PriceStep = message.PriceStep.Value;

				if (message.Decimals == null && security.Decimals == null)
					security.Decimals = message.PriceStep.Value.GetCachedDecimals();
			}

			if (message.Decimals != null)
			{
				if (isOverride || security.Decimals == null)
					security.Decimals = message.Decimals.Value;

				if (message.PriceStep == null && security.PriceStep == null)
					security.PriceStep = message.Decimals.Value.GetPriceStep();
			}

			if (!message.Name.IsEmpty())
			{
				if (isOverride || security.Name.IsEmpty())
					security.Name = message.Name;
			}

			if (!message.Class.IsEmpty())
			{
				if (isOverride || security.Class.IsEmpty())
					security.Class = message.Class;
			}

			if (message.OptionType != null)
			{
				if (isOverride || security.OptionType == null)
					security.OptionType = message.OptionType;
			}

			if (message.Strike != null)
			{
				if (isOverride || security.Strike == null)
					security.Strike = message.Strike.Value;
			}

			if (!message.BinaryOptionType.IsEmpty())
			{
				if (isOverride || security.BinaryOptionType == null)
					security.BinaryOptionType = message.BinaryOptionType;
			}

			if (message.SettlementDate != null)
			{
				if (isOverride || security.SettlementDate == null)
					security.SettlementDate = message.SettlementDate;
			}

			if (!message.ShortName.IsEmpty())
			{
				if (isOverride || security.ShortName.IsEmpty())
					security.ShortName = message.ShortName;
			}

			if (message.SecurityType != null)
			{
				if (isOverride || security.Type == null)
					security.Type = message.SecurityType.Value;
			}

			if (message.Shortable != null)
			{
				if (isOverride || security.Shortable == null)
					security.Shortable = message.Shortable.Value;
			}

			if (!message.CfiCode.IsEmpty())
			{
				if (isOverride || security.CfiCode.IsEmpty())
					security.CfiCode = message.CfiCode;

				if (security.Type == null)
					security.Type = security.CfiCode.Iso10962ToSecurityType();

				if (security.Type == SecurityTypes.Option && security.OptionType == null)
				{
					security.OptionType = security.CfiCode.Iso10962ToOptionType();

					//if (security.CfiCode.Length > 2)
					//	security.BinaryOptionType = security.CfiCode.Substring(2);
				}
			}

			if (!message.UnderlyingSecurityCode.IsEmpty())
			{
				if (isOverride || security.UnderlyingSecurityId.IsEmpty())
					security.UnderlyingSecurityId = message.UnderlyingSecurityCode + "@" + secId.BoardCode;
			}

			if (secId.HasExternalId())
			{
				if (isOverride || security.ExternalId.Equals(new SecurityExternalId()))
					security.ExternalId = secId.ToExternalId();
			}

			if (message.IssueDate != null)
			{
				if (isOverride || security.IssueDate == null)
					security.IssueDate = message.IssueDate.Value;
			}

			if (message.IssueSize != null)
			{
				if (isOverride || security.IssueSize == null)
					security.IssueSize = message.IssueSize.Value;
			}

			if (message.UnderlyingSecurityType != null)
			{
				if (isOverride || security.UnderlyingSecurityType == null)
					security.UnderlyingSecurityType = message.UnderlyingSecurityType.Value;
			}

			if (message.UnderlyingSecurityMinVolume != null)
			{
				if (isOverride || security.UnderlyingSecurityMinVolume == null)
					security.UnderlyingSecurityMinVolume = message.UnderlyingSecurityMinVolume.Value;
			}

			if (!message.BasketCode.IsEmpty())
			{
				if (isOverride || security.BasketCode.IsEmpty())
					security.BasketCode = message.BasketCode;
			}

			if (!message.BasketExpression.IsEmpty())
			{
				if (isOverride || security.BasketExpression.IsEmpty())
					security.BasketExpression = message.BasketExpression;
			}

			if (message.FaceValue != null)
			{
				if (isOverride || security.FaceValue == null)
					security.FaceValue = message.FaceValue;
			}

			if (message.OptionStyle != null)
			{
				if (isOverride || security.OptionStyle == null)
					security.OptionStyle = message.OptionStyle;
			}

			if (message.SettlementType != null)
			{
				if (isOverride || security.SettlementType == null)
					security.SettlementType = message.SettlementType;
			}

			if (message.PrimaryId != default)
			{
				if (isOverride || security.PrimaryId == default)
					security.PrimaryId = message.PrimaryId.ToStringId();
			}
		}

		/// <summary>
		/// To get the instrument by the identifier.
		/// </summary>
		/// <param name="provider">The provider of information about instruments.</param>
		/// <param name="id">Security ID.</param>
		/// <returns>The got instrument. If there is no instrument by given criteria, <see langword="null" /> is returned.</returns>
		public static Security LookupById(this ISecurityProvider provider, string id)
			=> provider.LookupByStringId(id);

		/// <summary>
		/// Lookup securities by criteria <paramref name="criteria" />.
		/// </summary>
		/// <param name="provider">The provider of information about instruments.</param>
		/// <param name="criteria">The instrument whose fields will be used as a filter.</param>
		/// <returns>Found instruments.</returns>
		public static IEnumerable<Security> Lookup(this ISecurityProvider provider, Security criteria)
		{
			if (provider == null)
				throw new ArgumentNullException(nameof(provider));

			return provider.Lookup(criteria.ToLookupMessage());
		}

		/// <summary>
		/// To get the instrument by the system identifier.
		/// </summary>
		/// <param name="provider">The provider of information about instruments.</param>
		/// <param name="nativeIdStorage">Security native identifier storage.</param>
		/// <param name="storageName">Storage name.</param>
		/// <param name="nativeId">Native (internal) trading system security id.</param>
		/// <returns>The got instrument. If there is no instrument by given criteria, <see langword="null" /> is returned.</returns>
		public static Security LookupByNativeId(this ISecurityProvider provider, INativeIdStorage nativeIdStorage, string storageName, object nativeId)
		{
			if (provider == null)
				throw new ArgumentNullException(nameof(provider));

			if (nativeIdStorage == null)
				throw new ArgumentNullException(nameof(nativeIdStorage));

			if (nativeId == null)
				throw new ArgumentNullException(nameof(nativeId));

			var secId = nativeIdStorage.TryGetByNativeId(storageName, nativeId);

			return secId == null ? null : provider.LookupById(secId.Value);
		}

		/// <summary>
		/// To get the instrument by the instrument code.
		/// </summary>
		/// <param name="provider">The provider of information about instruments.</param>
		/// <param name="code">Security code.</param>
		/// <param name="type">Security type.</param>
		/// <returns>The got instrument. If there is no instrument by given criteria, <see langword="null" /> is returned.</returns>
		public static IEnumerable<Security> LookupByCode(this ISecurityProvider provider, string code, SecurityTypes? type = null)
		{
			if (provider == null)
				throw new ArgumentNullException(nameof(provider));

			return code.IsEmpty() && type == null
				? provider.LookupAll()
				: provider.Lookup(new Security { Code = code, Type = type });
		}

		/// <summary>
		/// Lookup all securities predefined criteria.
		/// </summary>
		public static readonly Security LookupAllCriteria = new();

		/// <summary>
		/// Determine the <paramref name="criteria"/> contains lookup all filter.
		/// </summary>
		/// <param name="criteria">The instrument whose fields will be used as a filter.</param>
		/// <returns>Check result.</returns>
		public static bool IsLookupAll(this Security criteria)
		{
			if (criteria == null)
				throw new ArgumentNullException(nameof(criteria));

			if (criteria == LookupAllCriteria)
				return true;

			return criteria.ToLookupMessage().IsLookupAll();
		}

		/// <summary>
		/// Get all available instruments.
		/// </summary>
		/// <param name="provider">The provider of information about instruments.</param>
		/// <returns>All available instruments.</returns>
		public static IEnumerable<Security> LookupAll(this ISecurityProvider provider)
		{
			if (provider == null)
				throw new ArgumentNullException(nameof(provider));

			return provider.Lookup(Extensions.LookupAllCriteriaMessage);
		}

		/// <summary>
		/// Get or create (if not exist).
		/// </summary>
		/// <param name="storage">Securities meta info storage.</param>
		/// <param name="id">Security ID.</param>
		/// <param name="creator">Creator.</param>
		/// <param name="isNew">Is newly created.</param>
		/// <returns>Security.</returns>
		public static Security GetOrCreate(this ISecurityStorage storage, SecurityId id, Func<string, Security> creator, out bool isNew)
		{
			if (storage is null)
				throw new ArgumentNullException(nameof(storage));

			if (id == default)
				throw new ArgumentNullException(nameof(storage));

			if (creator is null)
				throw new ArgumentNullException(nameof(creator));

			lock (storage.SyncRoot)
			{
				var security = storage.LookupById(id);

				if (security == null)
				{
					security = creator(id.ToStringId());
					storage.Save(security, false);
					isNew = true;
				}
				else
					isNew = false;

				return security;
			}
		}

		/// <summary>
		/// Get or create (if not exist).
		/// </summary>
		/// <param name="storage">Storage.</param>
		/// <param name="portfolioName">Portfolio code name.</param>
		/// <param name="creator">Creator.</param>
		/// <param name="isNew">Is newly created.</param>
		/// <returns>Portfolio.</returns>
		public static Portfolio GetOrCreatePortfolio(this IPositionStorage storage, string portfolioName, Func<string, Portfolio> creator, out bool isNew)
		{
			if (storage is null)
				throw new ArgumentNullException(nameof(storage));

			if (creator is null)
				throw new ArgumentNullException(nameof(creator));

			lock (storage.SyncRoot)
			{
				var portfolio = storage.LookupByPortfolioName(portfolioName);

				if (portfolio == null)
				{
					portfolio = creator(portfolioName);
					storage.Save(portfolio);
					isNew = true;
				}
				else
					isNew = false;

				return portfolio;
			}
		}

		/// <summary>
		/// Get or create (if not exist).
		/// </summary>
		/// <param name="storage">Storage.</param>
		/// <param name="portfolio">Portfolio.</param>
		/// <param name="security">Security.</param>
		/// <param name="strategyId">Strategy ID.</param>
		/// <param name="side">Side.</param>
		/// <param name="clientCode">Client code.</param>
		/// <param name="depoName">Depo name.</param>
		/// <param name="limitType">Limit type.</param>
		/// <param name="creator">Creator.</param>
		/// <param name="isNew">Is newly created.</param>
		/// <returns>Position.</returns>
		public static Position GetOrCreatePosition(this IPositionStorage storage, Portfolio portfolio, Security security, string strategyId, Sides? side, string clientCode, string depoName, TPlusLimits? limitType, Func<Portfolio, Security, string, Sides?, string, string, TPlusLimits?, Position> creator, out bool isNew)
		{
			if (storage is null)
				throw new ArgumentNullException(nameof(storage));

			if (portfolio is null)
				throw new ArgumentNullException(nameof(portfolio));

			if (security is null)
				throw new ArgumentNullException(nameof(security));

			if (creator is null)
				throw new ArgumentNullException(nameof(creator));

			lock (storage.SyncRoot)
			{
				var position = storage.GetPosition(portfolio, security, strategyId, side, clientCode, depoName, limitType);

				if (position == null)
				{
					position = creator(portfolio, security, strategyId, side, clientCode, depoName, limitType);
					storage.Save(position);
					isNew = true;
				}
				else
					isNew = false;

				return position;
			}
		}

		/// <summary>
		/// To delete all instruments.
		/// </summary>
		/// <param name="storage">Securities meta info storage.</param>
		public static void DeleteAll(this ISecurityStorage storage)
		{
			if (storage is null)
				throw new ArgumentNullException(nameof(storage));

			storage.DeleteBy(Extensions.LookupAllCriteriaMessage);
		}

		/// <summary>
		/// To get the value of market data for the instrument.
		/// </summary>
		/// <typeparam name="T">The type of the market data field value.</typeparam>
		/// <param name="provider">The market data provider.</param>
		/// <param name="security">Security.</param>
		/// <param name="field">Market-data field.</param>
		/// <returns>The field value. If no data, the <see langword="null" /> will be returned.</returns>
		public static T GetSecurityValue<T>(this IMarketDataProvider provider, Security security, Level1Fields field)
		{
			if (provider == null)
				throw new ArgumentNullException(nameof(provider));

			if (security == null)
				throw new ArgumentNullException(nameof(security));

			return (T)provider.GetSecurityValue(security, field);
		}

		/// <summary>
		/// To get all market data values for the instrument.
		/// </summary>
		/// <param name="provider">The market data provider.</param>
		/// <param name="security">Security.</param>
		/// <returns>Filed values. If there is no data, <see langword="null" /> is returned.</returns>
		public static IDictionary<Level1Fields, object> GetSecurityValues(this IMarketDataProvider provider, Security security)
		{
			if (provider == null)
				throw new ArgumentNullException(nameof(provider));

			if (security == null)
				throw new ArgumentNullException(nameof(security));

			var fields = provider.GetLevel1Fields(security).ToArray();

			if (fields.IsEmpty())
				return null;

			return fields.ToDictionary(f => f, f => provider.GetSecurityValue(security, f));
		}

		/// <summary>
		/// To get a board by its code. If board with the passed name does not exist, then it will be created.
		/// </summary>
		/// <param name="exchangeInfoProvider">Exchanges and trading boards provider.</param>
		/// <param name="code">Board code.</param>
		/// <param name="createBoard">The handler creating a board, if it is not found. If the value is <see langword="null" />, then the board is created by default initialization.</param>
		/// <returns>Exchange board.</returns>
		public static ExchangeBoard GetOrCreateBoard(this IExchangeInfoProvider exchangeInfoProvider, string code, Func<string, ExchangeBoard> createBoard = null)
		{
			return exchangeInfoProvider.GetOrCreateBoard(code, out _, createBoard);
		}

		/// <summary>
		/// To get a board by its code. If board with the passed name does not exist, then it will be created.
		/// </summary>
		/// <param name="exchangeInfoProvider">Exchanges and trading boards provider.</param>
		/// <param name="code">Board code.</param>
		/// <param name="isNew">Is newly created.</param>
		/// <param name="createBoard">The handler creating a board, if it is not found. If the value is <see langword="null" />, then the board is created by default initialization.</param>
		/// <returns>Exchange board.</returns>
		public static ExchangeBoard GetOrCreateBoard(this IExchangeInfoProvider exchangeInfoProvider, string code, out bool isNew, Func<string, ExchangeBoard> createBoard = null)
		{
			if (exchangeInfoProvider == null)
				throw new ArgumentNullException(nameof(exchangeInfoProvider));

			if (code.IsEmpty())
				throw new ArgumentNullException(nameof(code));

			isNew = false;

			//if (code.EqualsIgnoreCase("RTS"))
			//	return ExchangeBoard.Forts;

			var board = exchangeInfoProvider.TryGetExchangeBoard(code);

			if (board != null)
				return board;

			isNew = true;

			if (createBoard == null)
			{
				var exchange = exchangeInfoProvider.TryGetExchange(code);

				if (exchange == null)
				{
					exchange = new Exchange { Name = code };
					exchangeInfoProvider.Save(exchange);
				}

				board = new ExchangeBoard
				{
					Code = code,
					Exchange = exchange
				};
			}
			else
			{
				board = createBoard(code);

				if (exchangeInfoProvider.TryGetExchange(board.Exchange.Name) == null)
					exchangeInfoProvider.Save(board.Exchange);
			}

			exchangeInfoProvider.Save(board);

			return board;
		}

		/// <summary>
		/// Is MICEX board.
		/// </summary>
		/// <param name="board">Board to check.</param>
		/// <returns>Check result.</returns>
		public static bool IsMicex(this ExchangeBoard board)
		{
			if (board == null)
				throw new ArgumentNullException(nameof(board));

			return board.Exchange == Exchange.Moex && board != ExchangeBoard.Forts;
		}

		/// <summary>
		/// Is the UX exchange stock market board.
		/// </summary>
		/// <param name="board">Board to check.</param>
		/// <returns>Check result.</returns>
		public static bool IsUxStock(this ExchangeBoard board)
		{
			if (board == null)
				throw new ArgumentNullException(nameof(board));

			return board.Exchange == Exchange.Ux && board != ExchangeBoard.Ux;
		}

		/// <summary>
		/// "All securities" instance.
		/// </summary>
		public static Security AllSecurity { get; } = new Security
		{
			Id = Extensions.AllSecurityId,
			Code = SecurityId.AssociatedBoardCode,
			//Class = task.GetDisplayName(),
			Name = LocalizedStrings.AllSecurities,
			Board = ExchangeBoard.Associated,
		};

		/// <summary>
		/// "News" security instance.
		/// </summary>
		public static readonly Security NewsSecurity = new() { Id = SecurityId.News.ToStringId() };

		/// <summary>
		/// "Money" security instance.
		/// </summary>
		public static readonly Security MoneySecurity = new() { Id = SecurityId.Money.ToStringId() };

		/// <summary>
		/// Find <see cref="AllSecurity"/> instance in the specified provider.
		/// </summary>
		/// <param name="provider">The provider of information about instruments.</param>
		/// <returns>Found instance.</returns>
		public static Security GetAllSecurity(this ISecurityProvider provider)
		{
			return provider.LookupById(default);
		}

		/// <summary>
		/// Check if the specified security is <see cref="AllSecurity"/>.
		/// </summary>
		/// <param name="security">Security.</param>
		/// <returns><see langword="true"/>, if the specified security is <see cref="AllSecurity"/>, otherwise, <see langword="false"/>.</returns>
		public static bool IsAllSecurity(this Security security)
		{
			if (security == null)
				throw new ArgumentNullException(nameof(security));

			return security == AllSecurity || security.Id.EqualsIgnoreCase(AllSecurity.Id);
		}

		/// <summary>
		/// To check the correctness of the entered identifier.
		/// </summary>
		/// <param name="id">Security ID.</param>
		/// <returns>An error message text, or <see langword="null" /> if no error.</returns>
		public static string ValidateId(ref string id)
		{
			//
			// can be fixed via TraderHelper.SecurityIdToFolderName
			//
			//var invalidChars = Path.GetInvalidFileNameChars().Where(id.Contains).ToArray();
			//if (invalidChars.Any())
			//{
			//	return invalidChars.Select(c => c.To<string>()).Join(", ");
			//}

			var firstIndex = id.IndexOf('@');

			if (firstIndex == -1)
			{
				id += "@ALL";
				//return LocalizedStrings.BoardNotSpecified;
			}

			var lastIndex = id.LastIndexOf('@');

			if (firstIndex != lastIndex)
				return null;

			if (firstIndex == 0)
				return LocalizedStrings.SecCodeNotFilled;
			else if (firstIndex == (id.Length - 1))
				return LocalizedStrings.BoardNotSpecified;

			return null;
		}

		/// <summary>
		/// Convert depths to quotes.
		/// </summary>
		/// <param name="messages">Depths.</param>
		/// <returns>Quotes.</returns>
		public static IEnumerable<TimeQuoteChange> ToTimeQuotes(this IEnumerable<QuoteChangeMessage> messages)
		{
			if (messages == null)
				throw new ArgumentNullException(nameof(messages));

			return messages.SelectMany(d => d.ToTimeQuotes());
		}

		/// <summary>
		/// Convert depth to quotes.
		/// </summary>
		/// <param name="message">Depth.</param>
		/// <returns>Quotes.</returns>
		public static IEnumerable<TimeQuoteChange> ToTimeQuotes(this QuoteChangeMessage message)
		{
			if (message == null)
				throw new ArgumentNullException(nameof(message));

			return message.Asks.Select(q => new TimeQuoteChange(Sides.Sell, q, message)).Concat(message.Bids.Select(q => new TimeQuoteChange(Sides.Buy, q, message))).OrderByDescending(q => q.Quote.Price);
		}

		/// <summary>
		/// Is specified security id associated with the board.
		/// </summary>
		/// <param name="securityId">Security ID.</param>
		/// <param name="board">Board info.</param>
		/// <returns><see langword="true" />, if associated, otherwise, <see langword="false"/>.</returns>
		public static bool IsAssociated(this SecurityId securityId, ExchangeBoard board)
		{
			if (board == null)
				throw new ArgumentNullException(nameof(board));

			return securityId.IsAssociated(board.Code);
		}

		/// <summary>
		/// Lookup securities, portfolios and orders.
		/// </summary>
		/// <param name="connector">The connection of interaction with trade systems.</param>
		public static void LookupAll(this Connector connector)
		{
			if (connector is null)
				throw new ArgumentNullException(nameof(connector));

			connector.Subscribe(DataType.Board.ToSubscription());
			connector.Subscribe(DataType.Securities.ToSubscription());
			connector.Subscribe(DataType.PositionChanges.ToSubscription());
			connector.Subscribe(DataType.Transactions.ToSubscription());
		}

		/// <summary>
		/// Get adapter by portfolio.
		/// </summary>
		/// <param name="portfolioProvider">The portfolio based message adapter's provider.</param>
		/// <param name="adapterProvider">The message adapter's provider.</param>
		/// <param name="portfolio">Portfolio.</param>
		/// <returns>Found adapter or <see langword="null"/>.</returns>
		public static IMessageAdapter TryGetAdapter(this IPortfolioMessageAdapterProvider portfolioProvider, IMessageAdapterProvider adapterProvider, Portfolio portfolio)
		{
			if (portfolioProvider == null)
				throw new ArgumentNullException(nameof(portfolioProvider));

			if (adapterProvider == null)
				throw new ArgumentNullException(nameof(adapterProvider));

			if (portfolio == null)
				throw new ArgumentNullException(nameof(portfolio));

			return portfolioProvider.TryGetAdapter(adapterProvider.CurrentAdapters, portfolio);
		}

		/// <summary>
		/// Get adapter by portfolio.
		/// </summary>
		/// <param name="portfolioProvider">The portfolio based message adapter's provider.</param>
		/// <param name="adapters">All available adapters.</param>
		/// <param name="portfolio">Portfolio.</param>
		/// <returns>Found adapter or <see langword="null"/>.</returns>
		public static IMessageAdapter TryGetAdapter(this IPortfolioMessageAdapterProvider portfolioProvider, IEnumerable<IMessageAdapter> adapters, Portfolio portfolio)
		{
			if (portfolioProvider == null)
				throw new ArgumentNullException(nameof(portfolioProvider));

			if (adapters == null)
				throw new ArgumentNullException(nameof(adapters));

			if (portfolio == null)
				throw new ArgumentNullException(nameof(portfolio));

			var id = portfolioProvider.TryGetAdapter(portfolio.Name);

			if (id == null)
				return null;

			return adapters.FindById(id.Value);
		}

		/// <summary>
		/// Convert inner securities messages to basket.
		/// </summary>
		/// <typeparam name="TMessage">Message type.</typeparam>
		/// <param name="innerSecMessages">Inner securities messages.</param>
		/// <param name="security">Basket security.</param>
		/// <param name="processorProvider">Basket security processors provider.</param>
		/// <returns>Messages of basket securities.</returns>
		public static IEnumerable<TMessage> ToBasket<TMessage>(this IEnumerable<TMessage> innerSecMessages, Security security, IBasketSecurityProcessorProvider processorProvider)
			where TMessage : Message
		{
			var processor = processorProvider.CreateProcessor(security);

			return innerSecMessages.SelectMany(processor.Process).Cast<TMessage>();
		}

		/// <summary>
		/// Create market data processor for basket securities.
		/// </summary>
		/// <param name="processorProvider">Basket security processors provider.</param>
		/// <param name="security">Basket security.</param>
		/// <returns>Market data processor for basket securities.</returns>
		public static IBasketSecurityProcessor CreateProcessor(this IBasketSecurityProcessorProvider processorProvider, Security security)
		{
			if (processorProvider == null)
				throw new ArgumentNullException(nameof(processorProvider));

			if (security == null)
				throw new ArgumentNullException(nameof(security));

			return processorProvider.GetProcessorType(security.BasketCode).CreateInstance<IBasketSecurityProcessor>(security);
		}

		/// <summary>
		/// Is specified security is basket.
		/// </summary>
		/// <param name="security">Security.</param>
		/// <returns>Check result.</returns>
		public static bool IsBasket(this Security security)
		{
			if (security == null)
				throw new ArgumentNullException(nameof(security));

			return !security.BasketCode.IsEmpty();
		}

		/// <summary>
		/// Is specified security is index.
		/// </summary>
		/// <param name="security">Security.</param>
		/// <returns>Check result.</returns>
		public static bool IsIndex(this Security security)
		{
			if (security == null)
				throw new ArgumentNullException(nameof(security));

			return security.BasketCode is "WI" or "EI";
		}

		/// <summary>
		/// Is specified security is continuous.
		/// </summary>
		/// <param name="security">Security.</param>
		/// <returns>Check result.</returns>
		public static bool IsContinuous(this Security security)
		{
			if (security == null)
				throw new ArgumentNullException(nameof(security));

			return security.BasketCode is "CE" or "CV";
		}

		/// <summary>
		/// Is specified security is continuous.
		/// </summary>
		/// <param name="security">Security.</param>
		/// <returns>Check result.</returns>
		public static bool IsContinuous(this SecurityMessage security)
		{
			if (security == null)
				throw new ArgumentNullException(nameof(security));

			return security.BasketCode is "CE" or "CV";
		}

		/// <summary>
		/// Convert <see cref="Security"/> to <see cref="BasketSecurity"/> value.
		/// </summary>
		/// <param name="security">Security.</param>
		/// <param name="processorProvider">Basket security processors provider.</param>
		/// <returns>Instruments basket.</returns>
		public static BasketSecurity ToBasket(this Security security, IBasketSecurityProcessorProvider processorProvider)
		{
			if (security == null)
				throw new ArgumentNullException(nameof(security));

			if (processorProvider == null)
				throw new ArgumentNullException(nameof(processorProvider));

			var type = processorProvider.GetSecurityType(security.BasketCode);
			var basketSec = type.CreateInstance<BasketSecurity>();
			security.CopyTo(basketSec);
			return basketSec;
		}

		/// <summary>
		/// Convert <see cref="Security"/> to <see cref="BasketSecurity"/> value.
		/// </summary>
		/// <param name="security">Security.</param>
		/// <returns>Instruments basket.</returns>
		/// <typeparam name="TBasketSecurity">Basket security type.</typeparam>
		public static TBasketSecurity ToBasket<TBasketSecurity>(this Security security)
			where TBasketSecurity : BasketSecurity, new()
		{
			if (security == null)
				throw new ArgumentNullException(nameof(security));

			var basketSec = new TBasketSecurity();
			security.CopyTo(basketSec);
			return basketSec;
		}

		/// <summary>
		/// Filter boards by code criteria.
		/// </summary>
		/// <param name="provider">The exchange boards provider.</param>
		/// <param name="criteria">Criteria.</param>
		/// <returns>Found boards.</returns>
		public static IEnumerable<ExchangeBoard> LookupBoards(this IExchangeInfoProvider provider, BoardLookupMessage criteria)
		{
			if (provider == null)
				throw new ArgumentNullException(nameof(provider));

			return provider.Boards.Filter(criteria);
		}

		/// <summary>
		/// Filter boards by code criteria.
		/// </summary>
		/// <param name="provider">The exchange boards provider.</param>
		/// <param name="criteria">Criteria.</param>
		/// <returns>Found boards.</returns>
		public static IEnumerable<BoardMessage> LookupBoards2(this IExchangeInfoProvider provider, BoardLookupMessage criteria)
		{
			if (provider == null)
				throw new ArgumentNullException(nameof(provider));

			return provider.Boards.Select(b => b.ToMessage(criteria.TransactionId)).Filter(criteria);
		}

		/// <summary>
		/// Filter boards by code criteria.
		/// </summary>
		/// <param name="boards">All boards.</param>
		/// <param name="criteria">Criteria.</param>
		/// <returns>Found boards.</returns>
		public static IEnumerable<ExchangeBoard> Filter(this IEnumerable<ExchangeBoard> boards, BoardLookupMessage criteria)
			=> boards.Where(b => b.ToMessage().IsMatch(criteria));

		/// <summary>
		/// Filter portfolios by the specified criteria.
		/// </summary>
		/// <param name="portfolios">All portfolios.</param>
		/// <param name="criteria">Criteria.</param>
		/// <returns>Found portfolios.</returns>
		public static IEnumerable<Portfolio> Filter(this IEnumerable<Portfolio> portfolios, PortfolioLookupMessage criteria)
			=> portfolios.Where(p => p.ToMessage().IsMatch(criteria, false));

		/// <summary>
		/// Filter positions the specified criteria.
		/// </summary>
		/// <param name="positions">All positions.</param>
		/// <param name="criteria">Criteria.</param>
		/// <returns>Found positions.</returns>
		public static IEnumerable<Position> Filter(this IEnumerable<Position> positions, PortfolioLookupMessage criteria)
			=> positions.Where(p => p.ToChangeMessage().IsMatch(criteria, false));

		private static void DoConnect(this IMessageAdapter adapter, IEnumerable<Message> requests, bool waitResponse, Func<Message, bool> newMessage)
		{
			if (adapter is null)
				throw new ArgumentNullException(nameof(adapter));

			if (requests is null)
				throw new ArgumentNullException(nameof(requests));

			if (newMessage is null)
				throw new ArgumentNullException(nameof(newMessage));

			if (adapter.IsNativeIdentifiers && !adapter.StorageName.IsEmpty())
			{
				var nativeIdAdapter = adapter.FindAdapter<SecurityNativeIdMessageAdapter>();

				if (nativeIdAdapter != null)
				{
					foreach (var secIdMsg in requests.OfType<ISecurityIdMessage>())
					{
						var secId = secIdMsg.SecurityId;

						if (secId == default)
							continue;

						var native = nativeIdAdapter.Storage.TryGetBySecurityId(adapter.StorageName, secId);
						secId.Native = native;
						secIdMsg.SecurityId = secId;
					}
				}
			}

			var sync = new SyncObject();

			adapter.NewOutMessage += msg =>
			{
				try
				{
					if (msg is BaseConnectionMessage conMsg)
					{
						newMessage(msg);
						sync.PulseSignal(conMsg.Error);
					}
					else
					{
						var done = newMessage(msg);

						if (done)
							sync.PulseSignal();
					}
				}
				catch (Exception e)
				{
					sync.PulseSignal(e);
				}
			};

			Do.Invariant(() =>
			{
				adapter.SendInMessage(new ConnectMessage { Language = LocalizedStrings.ActiveLanguage });

				lock (sync)
				{
					if (!sync.WaitSignal(adapter.ReConnectionSettings.TimeOutInterval, out var error))
						throw new TimeoutException();

					if (error != null)
						throw new InvalidOperationException(LocalizedStrings.ErrorConnection, (Exception)error);
				}

				foreach (var request in requests)
				{
					if (request is ITransactionIdMessage transIdMsg && transIdMsg.TransactionId == 0)
						transIdMsg.TransactionId = adapter.TransactionIdGenerator.GetNextId();

					if (!adapter.SendInMessage(request))
					{
						// the real error will be later, so ignore here
						//throw new InvalidOperationException(LocalizedStrings.UnknownType.Put(request.Type));
					}
				}

				if (waitResponse)
				{
					lock (sync)
					{
						if (!sync.WaitSignal(TimeSpan.FromMinutes(2), out var error))
							throw new TimeoutException("Processing too long.");

						if (error != null)
							throw new InvalidOperationException(LocalizedStrings.DataProcessError, (Exception)error);
					}
				}

				adapter.SendInMessage(new DisconnectMessage());
			});
		}

		/// <summary>
		/// Upload data.
		/// </summary>
		/// <typeparam name="TMessage">Request type.</typeparam>
		/// <param name="adapter">Adapter.</param>
		/// <param name="messages">Messages.</param>
		public static void Upload<TMessage>(this IMessageAdapter adapter, IEnumerable<TMessage> messages)
			where TMessage : Message
		{
			adapter.DoConnect(messages,	false, _ => false);
		}

		/// <summary>
		/// Download data.
		/// </summary>
		/// <typeparam name="TResult">Result message.</typeparam>
		/// <param name="adapter">Adapter.</param>
		/// <param name="request">Request.</param>
		/// <returns>Downloaded data.</returns>
		public static IEnumerable<TResult> Download<TResult>(this IMessageAdapter adapter, Message request)
			where TResult : Message
		{
			var retVal = new List<TResult>();

			var transIdMsg = request as ITransactionIdMessage;
			var resultIsConnect = typeof(TResult) == typeof(ConnectMessage);
			var resultIsOrigIdMsg = typeof(TResult).Is<IOriginalTransactionIdMessage>();

			bool TransactionMessageHandler(ITransactionIdMessage req, IOriginalTransactionIdMessage resp)
			{
				if (resp.OriginalTransactionId != req.TransactionId)
					return false;

				if (resp is TResult resMsg)
					retVal.Add(resMsg);

				var err = (resp as SubscriptionResponseMessage)?.Error ??
				          (resp as ErrorMessage)?.Error;

				if (err != null)
					throw err;

				return resp is SubscriptionFinishedMessage;
			}

			bool OtherMessageHandler(Message msg)
			{
				if (msg is TResult resMsg)
					retVal.Add(resMsg);

				if (msg is IErrorMessage errMsg && errMsg.Error != null)
					throw errMsg.Error;

				return msg is SubscriptionFinishedMessage;
			}

			adapter.DoConnect(request is null ? Enumerable.Empty<Message>() : new[] { request }, !resultIsConnect,
				msg => transIdMsg != null && resultIsOrigIdMsg ? msg is IOriginalTransactionIdMessage origIdMsg && TransactionMessageHandler(transIdMsg, origIdMsg) : OtherMessageHandler(msg));

			return retVal;
		}

		/// <summary>
		/// To get level1 market data.
		/// </summary>
		/// <param name="adapter">Adapter.</param>
		/// <param name="securityId">Security ID.</param>
		/// <param name="beginDate">Start date.</param>
		/// <param name="endDate">End date.</param>
		/// <param name="maxCount"><see cref="MarketDataMessage.Count"/></param>
		/// <param name="fields">Market data fields.</param>
		/// <param name="secType"><see cref="SecurityMessage.SecurityType"/>.</param>
		/// <returns>Level1 market data.</returns>
		public static IEnumerable<Level1ChangeMessage> GetLevel1(this IMessageAdapter adapter, SecurityId securityId, DateTime beginDate, DateTime endDate, int? maxCount = default, IEnumerable<Level1Fields> fields = default, SecurityTypes? secType = default)
		{
			var mdMsg = new MarketDataMessage
			{
				SecurityId = securityId,
				IsSubscribe = true,
				DataType2 = DataType.Level1,
				From = beginDate,
				To = endDate,
				Fields = fields,
				SecurityType = secType,
				Count = maxCount,
			};

			return adapter.Download<Level1ChangeMessage>(mdMsg);
		}

		/// <summary>
		/// To get tick data.
		/// </summary>
		/// <param name="adapter">Adapter.</param>
		/// <param name="securityId">Security ID.</param>
		/// <param name="beginDate">Start date.</param>
		/// <param name="endDate">End date.</param>
		/// <param name="maxCount"><see cref="MarketDataMessage.Count"/></param>
		/// <param name="secType"><see cref="SecurityMessage.SecurityType"/>.</param>
		/// <returns>Tick data.</returns>
		public static IEnumerable<ExecutionMessage> GetTicks(this IMessageAdapter adapter, SecurityId securityId, DateTime beginDate, DateTime endDate, int? maxCount = default, SecurityTypes? secType = default)
		{
			var mdMsg = new MarketDataMessage
			{
				SecurityId = securityId,
				IsSubscribe = true,
				DataType2 = DataType.Ticks,
				From = beginDate,
				To = endDate,
				SecurityType = secType,
				Count = maxCount,
			};

			return adapter.Download<ExecutionMessage>(mdMsg);
		}

		/// <summary>
		/// To get order log.
		/// </summary>
		/// <param name="adapter">Adapter.</param>
		/// <param name="securityId">Security ID.</param>
		/// <param name="beginDate">Start date.</param>
		/// <param name="endDate">End date.</param>
		/// <param name="maxCount"><see cref="MarketDataMessage.Count"/></param>
		/// <param name="secType"><see cref="SecurityMessage.SecurityType"/>.</param>
		/// <returns>Order log.</returns>
		public static IEnumerable<ExecutionMessage> GetOrderLog(this IMessageAdapter adapter, SecurityId securityId, DateTime beginDate, DateTime endDate, int? maxCount = default, SecurityTypes? secType = default)
		{
			var mdMsg = new MarketDataMessage
			{
				SecurityId = securityId,
				IsSubscribe = true,
				DataType2 = DataType.OrderLog,
				From = beginDate,
				To = endDate,
				SecurityType = secType,
				Count = maxCount,
			};

			return adapter.Download<ExecutionMessage>(mdMsg);
		}

		/// <summary>
		/// Download all securities.
		/// </summary>
		/// <param name="adapter">Adapter.</param>
		/// <param name="lookupMsg">Message security lookup for specified criteria.</param>
		/// <returns>All securities.</returns>
		public static IEnumerable<SecurityMessage> GetSecurities(this IMessageAdapter adapter, SecurityLookupMessage lookupMsg)
		{
			return adapter.Download<SecurityMessage>(lookupMsg);
		}

		/// <summary>
		/// To download candles.
		/// </summary>
		/// <param name="adapter">Adapter.</param>
		/// <param name="securityId">Security ID.</param>
		/// <param name="timeFrame">Time-frame.</param>
		/// <param name="from">Begin period.</param>
		/// <param name="to">End period.</param>
		/// <param name="count">Candles count.</param>
		/// <param name="buildField">Extra info for the <see cref="MarketDataMessage.BuildFrom"/>.</param>
		/// <param name="secType"><see cref="SecurityMessage.SecurityType"/>.</param>
		/// <returns>Downloaded candles.</returns>
		public static IEnumerable<TimeFrameCandleMessage> GetCandles(this IMessageAdapter adapter, SecurityId securityId, TimeSpan timeFrame, DateTimeOffset from, DateTimeOffset to, long? count = null, Level1Fields? buildField = null, SecurityTypes? secType = default)
		{
			var mdMsg = new MarketDataMessage
			{
				SecurityId = securityId,
				IsSubscribe = true,
				DataType2 = timeFrame.TimeFrame(),
				From = from,
				To = to,
				Count = count,
				BuildField = buildField,
				SecurityType = secType,
			};

			return adapter.Download<TimeFrameCandleMessage>(mdMsg);
		}

		/// <summary>
		/// Determines the specified portfolio is required.
		/// </summary>
		/// <param name="portfolio">Portfolio.</param>
		/// <param name="uniqueId">Portfolio identifier.</param>
		/// <returns>Check result.</returns>
		public static bool IsSame(this Portfolio portfolio, string uniqueId)
		{
			if (portfolio == null)
				throw new ArgumentNullException(nameof(portfolio));

			return portfolio.Name.EqualsIgnoreCase(uniqueId);// || (portfolio.InternalId != null && Guid.TryParse(uniqueId, out var indernalId) && portfolio.InternalId == indernalId);
		}

		/// <summary>
		/// Compile mathematical formula.
		/// </summary>
		/// <param name="expression">Text expression.</param>
		/// <param name="tracker"><see cref="AssemblyLoadContextTracker"/></param>
		/// <returns>Compiled mathematical formula.</returns>
		public static ExpressionFormula<decimal> Compile(this string expression, AssemblyLoadContextTracker tracker)
			=> Compile<decimal>(expression, tracker);

		private static class CacheHolder<TResult>
		{
			public static readonly SynchronizedDictionary<string, ExpressionFormula<TResult>> Cache = new();
		}

		/// <summary>
		/// Compile mathematical formula.
		/// </summary>
		/// <typeparam name="TResult">Result type.</typeparam>
		/// <param name="expression">Text expression.</param>
		/// <param name="tracker"><see cref="AssemblyLoadContextTracker"/></param>
		/// <returns>Compiled mathematical formula.</returns>
		public static ExpressionFormula<TResult> Compile<TResult>(this string expression, AssemblyLoadContextTracker tracker)
			=> CacheHolder<TResult>.Cache.SafeAdd(expression, key => ServicesRegistry.Compiler.Compile<TResult>(tracker, key, ServicesRegistry.TryCompilerCache));

		/// <summary>
		/// Create <see cref="IMessageAdapter"/>.
		/// </summary>
		/// <typeparam name="TAdapter">Adapter type.</typeparam>
		/// <param name="connector">The class to create connections to trading systems.</param>
		/// <param name="init">Initialize adapter.</param>
		/// <returns>The class to create connections to trading systems.</returns>
		public static TAdapter AddAdapter<TAdapter>(this Connector connector, Action<TAdapter> init)
			where TAdapter : IMessageAdapter
		{
			if (init is null)
				throw new ArgumentNullException(nameof(init));

			return (TAdapter)connector.AddAdapter(typeof(TAdapter), a => init((TAdapter)a));
		}

		/// <summary>
		/// Create <see cref="IMessageAdapter"/>.
		/// </summary>
		/// <param name="connector">The class to create connections to trading systems.</param>
		/// <param name="adapterType">Adapter type.</param>
		/// <param name="init">Initialize adapter.</param>
		/// <returns>The class to create connections to trading systems.</returns>
		public static IMessageAdapter AddAdapter(this Connector connector, Type adapterType, Action<IMessageAdapter> init)
		{
			if (connector is null)
				throw new ArgumentNullException(nameof(connector));

			if (adapterType is null)
				throw new ArgumentNullException(nameof(adapterType));

			if (init is null)
				throw new ArgumentNullException(nameof(init));

			var adapter = adapterType.CreateAdapter(connector.TransactionIdGenerator);
			init(adapter);
			connector.Adapter.InnerAdapters.Add(adapter);
			return adapter;
		}

		/// <summary>
		/// Determines whether the specified news related with StockSharp.
		/// </summary>
		/// <param name="news">News.</param>
		/// <returns>Check result.</returns>
		public static bool IsStockSharp(this News news)
		{
			if (news == null)
				throw new ArgumentNullException(nameof(news));

			return news.Source.EqualsIgnoreCase(Extensions.NewsStockSharpSource);
		}

		/// <summary>
		/// Indicator value.
		/// </summary>
		public static DataType IndicatorValue { get; } = DataType.Create<IIndicatorValue>();//.Immutable();

		/// <summary>
		/// Generate <see cref="SecurityId"/> security.
		/// </summary>
		/// <param name="generator"><see cref="SecurityIdGenerator"/></param>
		/// <param name="secCode">Security code.</param>
		/// <param name="board">Security board.</param>
		/// <returns><see cref="Security.Id"/> security.</returns>
		public static string GenerateId(this SecurityIdGenerator generator, string secCode/*, string secClass*/, ExchangeBoard board)
		{
			if (board is null)
				throw new ArgumentNullException(nameof(board));

			return generator.GenerateId(secCode, board.Code);
		}

		/// <summary>
		/// Is type compatible.
		/// </summary>
		/// <typeparam name="T">Required type.</typeparam>
		/// <param name="type">Type.</param>
		/// <returns>Check result.</returns>
		public static bool IsRequiredType<T>(this Type type)
			=> IsRequiredType(type, typeof(T));

		/// <summary>
		/// Is type compatible.
		/// </summary>
		/// <param name="type">Type.</param>
		/// <param name="required">Required type.</param>
		/// <returns>Check result.</returns>
		public static bool IsRequiredType(this Type type, Type required)
		{
			if (type is null)
				throw new ArgumentNullException(nameof(type));

			if (required is null)
				throw new ArgumentNullException(nameof(required));

			return !type.IsAbstract && type.IsPublic && required.IsAssignableFrom(type);
		}

		/// <summary>
		/// Convert <see cref="ChannelStates"/> value to <see cref="ProcessStates"/>.
		/// </summary>
		/// <param name="state"><see cref="ChannelStates"/> value.</param>
		/// <returns><see cref="ProcessStates"/> value.</returns>
		public static ProcessStates ToProcessState(this ChannelStates state)
			=> state switch
			{
				ChannelStates.Starting or ChannelStates.Stopped => ProcessStates.Stopped,
				ChannelStates.Stopping => ProcessStates.Stopping,
				ChannelStates.Started or ChannelStates.Suspending or ChannelStates.Suspended => ProcessStates.Started,
				_ => throw new ArgumentOutOfRangeException(nameof(state), state, LocalizedStrings.InvalidValue)
			};

		/// <summary>
		/// Convert <see cref="ProcessStates"/> value to <see cref="ChannelStates"/>.
		/// </summary>
		/// <param name="state"><see cref="ProcessStates"/> value.</param>
		/// <returns><see cref="ChannelStates"/> value.</returns>
		public static ChannelStates ToChannelState(this ProcessStates state)
			=> state switch
			{
				ProcessStates.Stopped => ChannelStates.Stopped,
				ProcessStates.Stopping => ChannelStates.Stopping,
				ProcessStates.Started => ChannelStates.Started,
				_ => throw new ArgumentOutOfRangeException(nameof(state), state, LocalizedStrings.InvalidValue)
			};
	}
}