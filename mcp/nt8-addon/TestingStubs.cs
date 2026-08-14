#if TESTING
// Minimal stubs for types used in RiskGuardAddOn.cs only available in NinjaTrader runtime
using System;
using System.Collections;
using System.Collections.Generic;
using NinjaTrader.Cbi;

namespace NinjaTrader.Data
{
    public class BarsPeriod
    {
        public BarsPeriodType BarsPeriodType { get; set; }
        public int Value { get; set; }
    }
    public enum BarsPeriodType { Minute, Day, Hour }

    public enum ErrorCode { NoError, GeneralError, NotImplemented, DataNotAvailable }

    public class Bars
    {
        public int Count { get; set; }
        private readonly double[] _high;
        private readonly double[] _low;
        private readonly double[] _close;
        private readonly double[] _open;
        private readonly long[] _volume;
        private readonly DateTime[] _time;

        public Bars(double[] high, double[] low, double[] close, double[] open, long[] volume, DateTime[] time)
        {
            _high = high; _low = low; _close = close; _open = open; _volume = volume; _time = time;
            Count = close != null ? close.Length : 0;
        }

        public double GetHigh(int idx) => _high[idx];
        public double GetLow(int idx) => _low[idx];
        public double GetClose(int idx) => _close[idx];
        public double GetOpen(int idx) => _open[idx];
        public long GetVolume(int idx) => _volume[idx];
        public DateTime GetTime(int idx) => _time[idx];
    }

    public class BarsRequest : IDisposable
    {
        public Instrument Instrument { get; }
        public int Count { get; }
        public BarsPeriod BarsPeriod { get; set; }
        public Bars Bars { get; set; }
        public Action<BarsRequest, ErrorCode, string> Callback { get; set; }

        public BarsRequest(Instrument instrument, int count)
        {
            Instrument = instrument;
            Count = count;
        }

        public void Request(Action<BarsRequest, ErrorCode, string> callback)
        {
            Callback = callback;
            if (TestBarsFactory != null)
            {
                Bars = TestBarsFactory(this);
            }
            callback(this, Bars != null ? ErrorCode.NoError : ErrorCode.GeneralError, Bars != null ? null : "No test bars supplied");
        }

        public void Dispose() { }

        public static Func<BarsRequest, Bars> TestBarsFactory { get; set; }
    }
}

namespace NinjaTrader.Cbi
{
    public enum ConnectionStatus { Connected, Disconnected, Connecting, ConnectionLost }
}

#endif
