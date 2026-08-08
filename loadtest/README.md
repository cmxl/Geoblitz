# k6 Load Tests

Performance load tests for the Geoblitz API using k6.

## Running the Tests

### Prerequisites

- k6 installed: https://k6.io/docs/getting-started/installation/
- API running (see below)

### Start the API

In one terminal:

```bash
dotnet run -c Release --project src/Geoblitz.Api
```

The API will start on `http://localhost:5235` by default.

### Run the Load Tests

In another terminal:

```bash
k6 run loadtest/mixed.js
```

To run a different test:

```bash
k6 run loadtest/distance.js
k6 run loadtest/nearest.js
k6 run loadtest/within.js
```

### Configuration

Set the `BASE_URL` environment variable to point to a different API endpoint:

```bash
BASE_URL=http://example.com:8080 k6 run loadtest/mixed.js
```

## Test Scripts

- **mixed.js**: Mixed traffic across all four endpoints (nearest, within, distance, geohash) with realistic distribution (40% nearest, 30% within, 20% distance, 10% geohash). Includes cache-busting random coordinates (~70%) and hot repeated coordinates (~30%).
- **distance.js**: Single-endpoint test for the `/distance` endpoint.
- **nearest.js**: Single-endpoint test for the `/cities/nearest` endpoint (count=10).
- **within.js**: Single-endpoint test for the `/cities/within` endpoint (radiusKm=100).

## Performance Thresholds

The load tests include initial performance thresholds:

- **p(95) < 20ms**: 95th percentile response time should be under 20ms
- **p(99) < 50ms**: 99th percentile response time should be under 50ms
- **error rate < 0.1%**: Failed request rate should stay below 0.1%

These thresholds are initial estimates and should be calibrated based on actual results from your environment and performance goals. Adjust them in the script's `thresholds` block after the first real run.

## Load Profile

By default, the mixed test runs:

- **32 concurrent virtual users (VUs)**
- **30 seconds duration**

Adjust the `scenarios` block in the script to modify load parameters.
