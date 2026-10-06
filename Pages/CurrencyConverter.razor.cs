using ChartJs.Blazor.Common;
using ChartJs.Blazor.Common.Axes;
using ChartJs.Blazor.Common.Axes.Ticks;
using ChartJs.Blazor.Common.Time;
using ChartJs.Blazor.LineChart;
using Microsoft.AspNetCore.Components;
using System.Data;
using ToolsServer.Shared;
using static ToolsServer.Enums;

namespace ToolsServer.Pages
{
    public partial class CurrencyConverter
    {
        [CascadingParameter]
        public MainLayout Layout { get; set; }
        private decimal sourceAmount = 1;
        private string sourceCurrency = "EUR";
        private decimal targetAmount;
        private string targetCurrency = "USD";
        [Inject]
        private HttpClient HttpClient { get; set; }
        private List<Currency> currencies { get; set; } = new();
        private List<HistoricalRates> historicalRatesList { get; set; } = new();
        public Dictionary<string, decimal> Rates { get; set; }
        private bool _render = false;
        private bool isLoading = false;
        private LineConfig chartConfig;
        private GraphTimePeriod timePeriod = GraphTimePeriod.Year;
        private string baseUrl = "http://api.currencylayer.com";
        private string token = "?access_key=c6c00bbee25c5439311ece83b0967f43";

        protected override async Task OnInitializedAsync()
        {
            base.OnInitialized();
            Layout.Title = "Currency Converter";
            isLoading = true;
            StateHasChanged();
            currencies = await GetCurrencies();
            await PrepareData(firstLoad:true);
        }

        private async Task OnSourceRateChange(ChangeEventArgs e)
        {
            if (decimal.TryParse(e.Value.ToString(), out var value) && sourceAmount != value)
            {
                sourceAmount = value;
                targetAmount = Convert();
            }
        }

        private async Task OnTargetRateChange(ChangeEventArgs e)
        {
            if (decimal.TryParse(e.Value.ToString(), out var value) && targetAmount != value)
            {
                targetAmount = value;
                sourceAmount = Convert(true);
            }
        }

        private async Task OnSourceCurrencyChange(ChangeEventArgs e)
        {
            sourceCurrency = e.Value.ToString();
            await PrepareData();
        }

        private async Task OnTargetCurrencyChange(ChangeEventArgs e)
        {
            targetCurrency = e.Value.ToString();
            await PrepareData(fromTarget:true);
        }

        private async Task PrepareData(bool firstLoad = false, bool fromTarget = false)
        {
            if (!fromTarget)
            {
                isLoading = true;
                StateHasChanged();
                //Rates = await GetRates(sourceCurrency);
            }

            if (firstLoad)
                _render = true;

            if (!fromTarget)
            {
                await GetHistoricalRates(DateTime.Today.AddYears(-1), DateTime.Today);
            }

            Rates = (historicalRatesList
                .Where(w => w.Date.Date == DateTime.Today)
                .Select(s => s.Rates)
                .FirstOrDefault()) ?? new Dictionary<string, decimal>();

            targetAmount = Convert();

            await ConfigGraph(firstLoad);
            if (!fromTarget)
                isLoading = false;

            StateHasChanged();
        }

        private decimal Convert(bool reverse = false)
        {
            if (reverse)
                return Math.Round(targetAmount / Rates.GetValueOrDefault(targetCurrency), 3);
            else
                return Math.Round(Rates.GetValueOrDefault(targetCurrency) * sourceAmount, 3);
        }

        private async Task<List<Currency>> GetCurrencies()
        {
            var response = await HttpClient.GetFromJsonAsync<CurrenciesResponse>($"{baseUrl}/list{token}");
            return response.Currencies?.Select(s => new Currency { Code = s.Key, Description = s.Value }).ToList() ?? new List<Currency>();
        }

        private async Task<decimal> ConvertAmount(string sourceCurrency, string targetCurrency, decimal amount)
        {
            var response = await HttpClient.GetFromJsonAsync<ConversionResponse>($"{baseUrl}/convert{token}&from={sourceCurrency}&to={targetCurrency}&amount={amount}");
            return response.result;
        }

        private async Task<Dictionary<string, decimal>> GetRates(string currency)
        {
            await Task.Delay(1000);
            var response = await HttpClient.GetFromJsonAsync<RatesResponse>($"{baseUrl}/live{token}&source={currency}");
            return response.quotes?.ToDictionary(s => s.Key.Replace(currency, string.Empty), s => s.Value) ?? new Dictionary<string, decimal>();
        }

        public async Task GetHistoricalRates(DateTime from, DateTime to)
        {
            await Task.Delay(1000);
            var url = $"{baseUrl}/timeframe{token}&source={sourceCurrency}&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}";
            var response = await HttpClient.GetFromJsonAsync<HistoricalRatesResponse>(url);
            historicalRatesList = response.quotes?.Select(s => new HistoricalRates
            {
                Date = DateTime.ParseExact(s.Key, "yyyy-MM-dd", null),
                Rates = s.Value?.ToDictionary(s => s.Key.Replace(sourceCurrency, string.Empty), s => s.Value) ?? new Dictionary<string, decimal>()
            }).ToList() ?? new List<HistoricalRates>();
        }

        private async Task ConfigGraph(bool firstLoad = false)
        {
            if (firstLoad)
            {
                chartConfig = new LineConfig
                {
                    Options = new LineOptions
                    {
                        Responsive = true,
                        Title = new OptionsTitle
                        {
                            Display = true,
                            Text = "Rate Graph"
                        },
                        Legend = new Legend
                        {
                            Display = false
                        },
                        Scales = new Scales
                        {
                            XAxes = new List<CartesianAxis>
                            {
                                new TimeAxis
                                {
                                    Ticks = new TimeTicks
                                    {
                                        Display = timePeriod != GraphTimePeriod.Week
                                    },
                                    Time = new TimeOptions
                                    {
                                        TooltipFormat = "DD MMM yyyy"
                                    },
                                    GridLines = new GridLines
                                    {
                                        DrawTicks = true,
                                        ZeroLineWidth = 0,
                                        OffsetGridLines = true
                                    },
                                }
                            },
                            YAxes = new List<CartesianAxis>
                            {
                                new LinearCartesianAxis
                                {
                                    Ticks = new LinearCartesianTicks
                                    {
                                        Precision = 2
                                    },
                                    GridLines = new GridLines
                                    {
                                        DrawTicks = true,
                                        ZeroLineWidth = 0,
                                        OffsetGridLines = true
                                    }
                                }
                            }
                        }
                    }
                };
            }

            var tempData = historicalRatesList
                .Where(w => (timePeriod == GraphTimePeriod.Week && w.Date.Ticks > DateTime.Today.AddDays(-7).Ticks) ||
                            (timePeriod == GraphTimePeriod.Month && w.Date.Ticks > DateTime.Today.AddMonths(-1).Ticks) ||
                            (timePeriod == GraphTimePeriod.Year && w.Date.Ticks > DateTime.Today.AddYears(-1).Ticks))
                .OrderBy(o => o.Date)
                .ToList();

            chartConfig.Data.Labels.Clear();
            chartConfig.Data.Datasets.Clear();

            tempData.ForEach(f => chartConfig.Data.Labels.Add(f.Date.ToString("yyyy-MM-dd")));
            chartConfig.Data.Datasets.Add(new LineDataset<decimal>(tempData.Select(s => s.Rates.GetValueOrDefault(targetCurrency))));
            ((TimeAxis)chartConfig.Options.Scales.XAxes.First()).Ticks.Display = timePeriod != GraphTimePeriod.Week;
        }

        internal class Currency
        {
            public string Code { get; set; }
            public string Description { get; set; }
        }

        internal class HistoricalRates
        {
            public DateTime Date { get; set; }
            public Dictionary<string, decimal> Rates { get; set; }
        }

        internal class CurrenciesResponse
        {
            public Dictionary<string, string> Currencies { get; set; }
        }

        internal class ConversionResponse
        {
            public decimal result { get; set; }
        }

        internal class RatesResponse
        {
            public Dictionary<string, decimal> quotes { get; set; }
        }

        internal class HistoricalRatesResponse
        {
            public Dictionary<string, Dictionary<string, decimal>> quotes { get; set; }
        }
    }
}
