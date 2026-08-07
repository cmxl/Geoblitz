import http from 'k6/http';
import { check } from 'k6';

export const options = {
    scenarios: {
        mixed: { executor: 'constant-vus', vus: 32, duration: '30s' },
    },
    thresholds: {
        http_req_duration: ['p(95)<20', 'p(99)<50'], // ms — tune after first real run
        http_req_failed: ['rate<0.001'],
    },
};

const BASE = __ENV.BASE_URL || 'http://localhost:5235';

// ~30% repeated hot coordinates (cache hits), ~70% random (compute path)
function coords() {
    if (Math.random() < 0.3) return { lat: 52.52, lon: 13.405 };
    return { lat: (Math.random() * 180 - 90).toFixed(3), lon: (Math.random() * 360 - 180).toFixed(3) };
}

export default function () {
    const { lat, lon } = coords();
    const res = http.get(`${BASE}/cities/nearest?lat=${lat}&lon=${lon}&count=10`);
    check(res, { 'status 200': (r) => r.status === 200 });
}
