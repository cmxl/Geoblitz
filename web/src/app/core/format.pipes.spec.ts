import { describe, it, expect } from 'vitest';
import { KmPipe, EngineTimePipe } from './format.pipes';

describe('format pipes', () => {
  const km = new KmPipe();
  const et = new EngineTimePipe();

  it('formats kilometers to 3 decimals', () => {
    expect(km.transform(0.6123)).toBe('0.612 km');
    expect(km.transform(504.2)).toBe('504.200 km');
    expect(km.transform(null)).toBe('—');
  });

  it('formats engine time in µs below 1000, ms above', () => {
    expect(et.transform(9.1)).toBe('9.1 µs');
    expect(et.transform(999.94)).toBe('999.9 µs');
    expect(et.transform(1240)).toBe('1.24 ms');
    expect(et.transform(null)).toBe('—');
  });
});
