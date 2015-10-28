/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using com.fxcm.external.api.transport;
using com.fxcm.external.api.transport.listeners;
using com.fxcm.external.api.util;
using com.fxcm.fix;
using com.fxcm.fix.other;
using com.fxcm.fix.posttrade;
using com.fxcm.fix.pretrade;
using com.fxcm.fix.trade;
using com.fxcm.messaging;
using com.fxcm.messaging.util;

namespace TestFxcm
{
    public class FxcmTester : IGenericMessageListener, IStatusMessageListener
    {
        private readonly object _locker = new object();
        private IGateway _gateway;
        private string _currentRequest;

        private readonly Dictionary<string, TradingSecurity> _securities = new Dictionary<string, TradingSecurity>();
        private readonly Dictionary<string, MarketDataSnapshot> _rates = new Dictionary<string, MarketDataSnapshot>();
        private readonly Dictionary<string, CollateralReport> _accounts = new Dictionary<string, CollateralReport>();
        private readonly Dictionary<string, ExecutionReport> _orders = new Dictionary<string, ExecutionReport>();
        private readonly Dictionary<string, ExecutionReport> _orderRequests = new Dictionary<string, ExecutionReport>();

        private readonly AutoResetEvent _requestTradingSessionStatusEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent _requestAccountListEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent _requestPositionListEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent _requestOrderEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent _requestMarketDataEvent = new AutoResetEvent(false);

        private const string UserName = "username";
        private const string Password = "password";
        private const string Terminal = "Demo";
        private const string Server = "http://www.fxcorporate.com/Hosts.jsp";
        private const string Symbol = "EUR/USD";

        public void Run()
        {
            _gateway = GatewayFactory.createGateway();

            // register the generic message listener with the gateway
            _gateway.registerGenericMessageListener(this);

            // register the status message listener with the gateway
            _gateway.registerStatusMessageListener(this);

            // attempt to login with the local login properties
            Console.WriteLine("Logging in...");
            var loginProperties = new FXCMLoginProperties(UserName, Password, Terminal, Server);
            // disable the streaming rates (default automatic subscriptions)
            loginProperties.addProperty(IConnectionManager.__Fields.MSG_FLAGS, IFixDefs.__Fields.CHANNEL_MARKET_DATA.ToString());
            _gateway.login(loginProperties);
            Console.WriteLine("Logged in.\n");

            // get instrument list
            Console.WriteLine("Requesting trading session status...");
            lock (_locker)
            {
                _currentRequest = _gateway.requestTradingSessionStatus();
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestTradingSessionStatusEvent.WaitOne();
            Console.WriteLine("Instruments: {0}\n", _securities.Count);
            foreach (var pair in _securities)
            {
                var security = pair.Value;

                Console.WriteLine(security.getSymbol() + " " +
                                  security.getCurrency() + " " +
                                  security.getFXCMSubscriptionStatus() + " " +
                                  security.getRoundLot() + " " +
                                  security.getContractMultiplier() + " " +
                                  security.getFXCMMinQuantity() + " " +
                                  security.getFXCMMaxQuantity() + " " +
                                  security.getFXCMSymPointSize() + " " +
                                  security.getFactor() + " " +
                                  security.getFXCMSymPrecision() + " " +
                                  security.getProduct() + " " +
                                  security.getFXCMProductID() + " " +
                                  (security.getFXCMProductID() == IFixValueDefs.__Fields.FXCMPRODUCTID_FOREX ? "Forex" : "CFD"));
            }
            Console.WriteLine();

            // get account list
            Console.WriteLine("Requesting account list...");
            lock (_locker)
            {
                _currentRequest = _gateway.requestAccounts();
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestAccountListEvent.WaitOne();
            Console.WriteLine("Accounts: {0}\n", _accounts.Count);

            // use first account
            var accountId = _accounts.Keys.First();

            // we are unable to continue if Hedging enabled
            if (_accounts[accountId].getParties().getFXCMPositionMaintenance() == "Y")
                throw new NotSupportedException("The Lean engine does not support accounts with Hedging enabled. Please contact FXCM support to disable Hedging and try again.");

            // get open order list
            Console.WriteLine("Requesting open order list...");
            lock (_locker)
            {
                _currentRequest = _gateway.requestOpenOrders();
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestOrderEvent.WaitOne();
            Console.WriteLine("Open orders: {0}\n", _orders.Keys.Count);

            // cancel all open orders
            if (_orders.Keys.Count > 0)
            {
                Console.WriteLine("Cancelling open orders...");
                foreach (var order in _orders.Values.ToList())
                {
                    Console.WriteLine("Cancelling order {0}...", order.getOrderID());
                    var request = MessageGenerator.generateOrderCancelRequest("", order.getOrderID(), order.getSide(), order.getAccount());
                    lock (_locker)
                    {
                        _currentRequest = _gateway.sendMessage(request);
                        Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
                    }
                    _requestOrderEvent.WaitOne();
                    Console.WriteLine("Order {0} cancelled.", order.getOrderID());
                }
                _orders.Clear();
                Console.WriteLine("Open orders cancelled.\n");
            }

            // get open position list
            Console.WriteLine("Requesting open position list...");
            lock (_locker)
            {
                _currentRequest = _gateway.requestOpenPositions();
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestPositionListEvent.WaitOne();
            Console.WriteLine("Open position list complete.\n");

            // get closed position list
            Console.WriteLine("Requesting closed position list...");
            lock (_currentRequest)
            {
                _currentRequest = _gateway.requestClosedPositions();
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestPositionListEvent.WaitOne();
            Console.WriteLine("Closed position list complete.\n");

            // get current quotes for the instrument
            if (_securities.ContainsKey(Symbol))
            {
                Console.WriteLine("Requesting market data for " + Symbol + "...");
                var request = new MarketDataRequest();
                request.setMDEntryTypeSet(MarketDataRequest.MDENTRYTYPESET_ALL);
                request.setSubscriptionRequestType(SubscriptionRequestTypeFactory.SNAPSHOT);
                request.addRelatedSymbol(_securities[Symbol]);
                lock (_locker)
                {
                    _currentRequest = _gateway.sendMessage(request);
                    Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
                }
                _requestMarketDataEvent.WaitOne();
                Console.WriteLine(Symbol + " - Bid: {0} Ask: {1}\n", _rates[Symbol].getBidClose(), _rates[Symbol].getAskClose());
            }

            // submit limit order
            Console.WriteLine("Submitting limit order...");
            var limitOrderRequest = MessageGenerator.generateOpenOrder(0.7, accountId, 10000, SideFactory.BUY, Symbol, "");
            limitOrderRequest.setOrdType(OrdTypeFactory.LIMIT);
            limitOrderRequest.setTimeInForce(TimeInForceFactory.GOOD_TILL_CANCEL);
            lock (_locker)
            {
                _currentRequest = _gateway.sendMessage(limitOrderRequest);
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestOrderEvent.WaitOne();
            Console.WriteLine("Limit order submitted.\n");

            var limitOrder = _orderRequests[_currentRequest];

            // update limit order
            Console.WriteLine("Updating limit order...");
            var orderReplaceRequest = MessageGenerator.generateOrderReplaceRequest("", limitOrder.getOrderID(), limitOrder.getSide(), limitOrder.getOrdType(), 0.8, limitOrder.getAccount());
            lock (_locker)
            {
                _currentRequest = _gateway.sendMessage(orderReplaceRequest);
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestOrderEvent.WaitOne();
            Console.WriteLine("Limit order updated.\n");

            // cancel limit order
            Console.WriteLine("Cancelling limit order...");
            var orderCancelRequest = MessageGenerator.generateOrderCancelRequest("", limitOrder.getOrderID(), limitOrder.getSide(), limitOrder.getAccount());
            lock (_locker)
            {
                _currentRequest = _gateway.sendMessage(orderCancelRequest);
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestOrderEvent.WaitOne();
            Console.WriteLine("Limit order cancelled.\n");

            // submit stop market order
            Console.WriteLine("Submitting stop market order...");
            var stopMarketOrderRequest = MessageGenerator.generateOpenOrder(0.7, accountId, 10000, SideFactory.SELL, Symbol, "");
            stopMarketOrderRequest.setOrdType(OrdTypeFactory.STOP);
            stopMarketOrderRequest.setTimeInForce(TimeInForceFactory.GOOD_TILL_CANCEL);
            lock (_locker)
            {
                _currentRequest = _gateway.sendMessage(stopMarketOrderRequest);
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestOrderEvent.WaitOne();
            Console.WriteLine("Stop market order submitted.\n");

            var stopMarketOrder = _orderRequests[_currentRequest];

            // cancel stop market order
            Console.WriteLine("Cancelling stop market order...");
            orderCancelRequest = MessageGenerator.generateOrderCancelRequest("", stopMarketOrder.getOrderID(), stopMarketOrder.getSide(), stopMarketOrder.getAccount());
            lock (_locker)
            {
                _currentRequest = _gateway.sendMessage(orderCancelRequest);
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestOrderEvent.WaitOne();
            Console.WriteLine("Stop market order cancelled.\n");

            // submit stop limit order
            Console.WriteLine("Submitting stop limit order...");
            var stopLimitOrderRequest = MessageGenerator.generateOpenOrder(0.7, accountId, 10000, SideFactory.SELL, Symbol, "");
            stopLimitOrderRequest.setOrdType(OrdTypeFactory.STOP_LIMIT);
            stopLimitOrderRequest.setStopPx(0.695);
            stopLimitOrderRequest.setTimeInForce(TimeInForceFactory.GOOD_TILL_CANCEL);
            lock (_locker)
            {
                _currentRequest = _gateway.sendMessage(stopLimitOrderRequest);
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestOrderEvent.WaitOne();
            Console.WriteLine("Stop limit order submitted.\n");

            var stopLimitOrder = _orderRequests[_currentRequest];

            // cancel stop limit order
            Console.WriteLine("Cancelling stop limit order...");
            orderCancelRequest = MessageGenerator.generateOrderCancelRequest("", stopLimitOrder.getOrderID(), stopLimitOrder.getSide(), stopLimitOrder.getAccount());
            lock (_locker)
            {
                _currentRequest = _gateway.sendMessage(orderCancelRequest);
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestOrderEvent.WaitOne();
            Console.WriteLine("Stop limit order cancelled.\n");

            // submit market order (buy)
            Console.WriteLine("Submitting buy market order...");
            var buyOrder = MessageGenerator.generateMarketOrder(accountId, 10000, SideFactory.BUY, Symbol, "");
            lock (_locker)
            {
                _currentRequest = _gateway.sendMessage(buyOrder);
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestOrderEvent.WaitOne();
            Console.WriteLine("Buy market order submitted.\n");

            // submit market order (sell)
            Console.WriteLine("Submitting sell market order...");
            var sellOrder = MessageGenerator.generateMarketOrder(accountId, 10000, SideFactory.SELL, Symbol, "");
            lock (_locker)
            {
                _currentRequest = _gateway.sendMessage(sellOrder);
                Console.WriteLine("\tRequestId = {0}\n", _currentRequest);
            }
            _requestOrderEvent.WaitOne();
            Console.WriteLine("Sell market order submitted.\n");

            Console.WriteLine();
            Console.WriteLine("Press Return to Logout and exit.\n");
            Console.ReadKey();

            // log out
            Console.WriteLine("Logging out...");
            _gateway.logout();
            Console.WriteLine("Logged out.");

            // remove the generic message listener, stop listening to updates
            _gateway.removeGenericMessageListener(this);

            // remove the status message listener, stop listening to status changes
            _gateway.removeStatusMessageListener(this);
        }

        /// <summary>
        /// IStatusMessageListener implementation to capture and process messages sent back from API
        /// </summary>
        /// <param name="status">Status message received by API</param>
        public void messageArrived(ISessionStatus status)
        {
            // check the status code
            if (status.getStatusCode() == ISessionStatus.__Fields.STATUSCODE_ERROR ||
                status.getStatusCode() == ISessionStatus.__Fields.STATUSCODE_DISCONNECTING ||
                status.getStatusCode() == ISessionStatus.__Fields.STATUSCODE_CONNECTING ||
                status.getStatusCode() == ISessionStatus.__Fields.STATUSCODE_CONNECTED ||
                status.getStatusCode() == ISessionStatus.__Fields.STATUSCODE_CRITICAL_ERROR ||
                status.getStatusCode() == ISessionStatus.__Fields.STATUSCODE_EXPIRED ||
                status.getStatusCode() == ISessionStatus.__Fields.STATUSCODE_LOGGINGIN ||
                status.getStatusCode() == ISessionStatus.__Fields.STATUSCODE_LOGGEDIN ||
                status.getStatusCode() == ISessionStatus.__Fields.STATUSCODE_PROCESSING ||
                status.getStatusCode() == ISessionStatus.__Fields.STATUSCODE_DISCONNECTED)
            {
                // display status message
                Console.WriteLine("\t" + status.getStatusMessage());
            }
        }

        /// <summary>
        /// IGenericMessageListener implementation to capture and process messages sent back from API
        /// </summary>
        /// <param name="message">Message received for processing by API</param>
        public void messageArrived(ITransportable message)
        {
            // Dispatch message to specific handler

            lock (_locker)
            {
                if (message is TradingSessionStatus)
                    OnTradingSessionStatus((TradingSessionStatus) message);

                else if (message is CollateralReport)
                    OnCollateralReport((CollateralReport) message);

                else if (message is MarketDataSnapshot)
                    OnMarketDataSnapshot((MarketDataSnapshot)message);

                else if (message is ExecutionReport)
                    OnExecutionReport((ExecutionReport)message);

                else if (message is RequestForPositionsAck)
                    OnRequestForPositionsAck((RequestForPositionsAck)message);

                else if (message is PositionReport)
                    OnPositionReport((PositionReport)message);

                else if (message is OrderCancelReject)
                    OnOrderCancelReject((OrderCancelReject)message);

                // unhandled messages here
                else if (message is UserResponse || message is CollateralInquiryAck || message is MarketDataRequestReject) { }

                else
                    Console.WriteLine("Unhandled message: {0}\n", message);
            }
        }

        // TradingSessionStatus message handler
        private void OnTradingSessionStatus(TradingSessionStatus tradingSessionStatus)
        {
            Console.WriteLine("OnTradingSessionStatus()");
            Console.WriteLine("\tRequestId = {0}", tradingSessionStatus.getRequestID());
            Console.WriteLine();

            if (tradingSessionStatus.getRequestID() == _currentRequest)
            {
                var securities = tradingSessionStatus.getSecurities();
                while (securities.hasMoreElements())
                {
                    var security = (TradingSecurity) securities.nextElement();
                    _securities[security.getSymbol()] = security;
                }

                _requestTradingSessionStatusEvent.Set();
            }
        }

        // CollateralReport message handler
        private void OnCollateralReport(CollateralReport collateralReport)
        {
            Console.WriteLine("OnCollateralReport()");
            Console.WriteLine("\tRequestId = {0}", collateralReport.getRequestID());

            Console.WriteLine("\tgetAccount() = " + collateralReport.getAccount());
            Console.WriteLine("\tgetCashOutstanding() = " + collateralReport.getCashOutstanding());
            Console.WriteLine("\tgetStartCash() = " + collateralReport.getStartCash());
            Console.WriteLine("\tgetEndCash() = " + collateralReport.getEndCash());
            Console.WriteLine("\tgetFXCMMarginCall() = " + collateralReport.getFXCMMarginCall());
            Console.WriteLine("\tgetFXCMUsedMargin() = " + collateralReport.getFXCMUsedMargin());
            Console.WriteLine("\tgetFXCMUsedMargin3() = " + collateralReport.getFXCMUsedMargin3());
            Console.WriteLine("\tgetQuantity() = " + collateralReport.getQuantity());
            Console.WriteLine("\tgetParties() = " + collateralReport.getParties());
            Console.WriteLine();

            // add the trading account to the account list
            _accounts[collateralReport.getAccount()] = collateralReport;

            if (collateralReport.getRequestID() == _currentRequest)
            {
                // set the state of the request to be completed only if this is the last collateral report requested
                if (collateralReport.isLastRptRequested())
                    _requestAccountListEvent.Set();
            }
        }

        // MarketDataSnapshot message handler
        private void OnMarketDataSnapshot(MarketDataSnapshot marketDataSnapshot)
        {
            Console.WriteLine("OnMarketDataSnapshot()");
            Console.WriteLine("\tRequestId = {0}", marketDataSnapshot.getRequestID());
            Console.WriteLine();

            // update the current prices for the instrument
            _rates[marketDataSnapshot.getInstrument().getSymbol()] = marketDataSnapshot;

            if (marketDataSnapshot.getRequestID() == _currentRequest)
            {
                if (marketDataSnapshot.getFXCMContinuousFlag() == IFixValueDefs.__Fields.FXCMCONTINUOUS_END)
                    _requestMarketDataEvent.Set();
            }
        }

        // ExecutionReport message handler
        private void OnExecutionReport(ExecutionReport executionReport)
        {
            Console.WriteLine("OnExecutionReport()");
            Console.WriteLine("\tRequestId = {0}", executionReport.getRequestID());

            Console.WriteLine("\tgetOrderID() = " + executionReport.getOrderID());
            Console.WriteLine("\tgetFXCMPosID() = " + executionReport.getFXCMPosID());
            Console.WriteLine("\tgetAccount() = " + executionReport.getAccount());
            Console.WriteLine("\tgetTransactTime() = " + executionReport.getTransactTime());
            Console.WriteLine("\tgetExecType() = " + executionReport.getExecType());
            Console.WriteLine("\tgetFXCMOrdStatus() = " + executionReport.getFXCMOrdStatus());
            Console.WriteLine("\tgetOrdStatus() = " + executionReport.getOrdStatus());
            Console.WriteLine("\tgetPrice() = " + executionReport.getPrice());
            Console.WriteLine("\tgetStopPx() = " + executionReport.getStopPx());
            Console.WriteLine("\tgetOrderQty() = " + executionReport.getOrderQty());
            Console.WriteLine("\tgetCumQty() = " + executionReport.getCumQty());
            Console.WriteLine("\tgetLastQty() = " + executionReport.getLastQty());
            Console.WriteLine("\tgetLeavesQty() = " + executionReport.getLeavesQty());
            Console.WriteLine("\tgetFXCMErrorDetails() = " + executionReport.getFXCMErrorDetails());
            Console.WriteLine();

            var orderId = executionReport.getOrderID();
            if (orderId != "NONE")
            {
                _orders[orderId] = executionReport;
                _orderRequests[executionReport.getRequestID()] = executionReport;
            }

            if (executionReport.getRequestID() == _currentRequest)
            {
                var status = executionReport.getFXCMOrdStatus().getCode();
                if (executionReport.isLastRptRequested() && 
                    status != IFixValueDefs.__Fields.FXCMORDSTATUS_PENDING_CANCEL &&
                    status != IFixValueDefs.__Fields.FXCMORDSTATUS_EXECUTING &&
                    status != IFixValueDefs.__Fields.FXCMORDSTATUS_INPROCESS)
                {
                    _requestOrderEvent.Set();
                }
            }
        }

        // RequestForPositionsAck message handler
        private void OnRequestForPositionsAck(RequestForPositionsAck requestForPositionsAck)
        {
            Console.WriteLine("OnRequestForPositionsAck()");
            Console.WriteLine("\tRequestId = {0}", requestForPositionsAck.getRequestID());

            Console.WriteLine("\tgetAccount() = " + requestForPositionsAck.getAccount());
            Console.WriteLine("\tgetTotalNumPosReports() = " + requestForPositionsAck.getTotalNumPosReports());
            Console.WriteLine("\tgetPosReqStatus() = " + requestForPositionsAck.getPosReqStatus());
            Console.WriteLine("\tgetFXCMErrorDetails() = " + requestForPositionsAck.getFXCMErrorDetails());
            Console.WriteLine();

            if (requestForPositionsAck.getRequestID() == _currentRequest)
            {
                if (requestForPositionsAck.getTotalNumPosReports() == 0)
                {
                    _requestPositionListEvent.Set();
                }
            }
        }

        // PositionReport message handler
        private void OnPositionReport(PositionReport positionReport)
        {
            Console.WriteLine("OnPositionReport()");
            Console.WriteLine("\tRequestId = {0}", positionReport.getRequestID());

            Console.WriteLine("\tgetAccount() = " + positionReport.getAccount());
            Console.WriteLine("\tgetInstrument().getSymbol() = " + positionReport.getInstrument().getSymbol());
            Console.WriteLine("\tgetCurrency() = " + positionReport.getCurrency());
            Console.WriteLine("\tgetPositionQty() = " + positionReport.getPositionQty());
            Console.WriteLine("\tgetSettlPrice() = " + positionReport.getSettlPrice());
            Console.WriteLine();

            if (positionReport.getRequestID() == _currentRequest)
            {
                if (positionReport.isLastRptRequested())
                {
                    _requestPositionListEvent.Set();
                }
            }
        }

        // OrderCancelReject message handler
        private void OnOrderCancelReject(OrderCancelReject orderCancelReject)
        {
            Console.WriteLine("OnOrderCancelReject()");
            Console.WriteLine("\tRequestId = {0}", orderCancelReject.getRequestID());

            Console.WriteLine("\tgetAccount() = " + orderCancelReject.getAccount());
            Console.WriteLine("\tgetOrderID() = " + orderCancelReject.getOrderID());
            Console.WriteLine("\tgetCxlRejReason() = " + orderCancelReject.getCxlRejReason());
            Console.WriteLine("\tgetFXCMErrorDetails() = " + orderCancelReject.getFXCMErrorDetails());
            Console.WriteLine();

            if (orderCancelReject.getRequestID() == _currentRequest)
            {
                _requestOrderEvent.Set();
            }
        }

    }
}