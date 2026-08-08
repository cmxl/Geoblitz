import { TestBed } from '@angular/core/testing';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { QueryPanelComponent } from './query-panel.component';
import { GeoQueryStore } from '../core/geo-query.store';
import { GeoApiService } from '../core/geo-api.service';

describe('QueryPanelComponent', () => {
  let api: { nearest: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    api = { nearest: vi.fn() };
    TestBed.configureTestingModule({ providers: [{ provide: GeoApiService, useValue: api }] });
  });

  function render() {
    const fixture = TestBed.createComponent(QueryPanelComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('switches store mode from the mode buttons', () => {
    const fixture = render();
    const store = TestBed.inject(GeoQueryStore);
    const buttons = fixture.nativeElement.querySelectorAll('button.mode');
    expect(buttons.length).toBe(3);
    (buttons[1] as HTMLButtonElement).click();
    expect(store.mode()).toBe('within');
  });

  it('renders results with rank, country and distance', async () => {
    api.nearest.mockResolvedValue({
      data: {
        count: 2,
        cities: [
          {
            name: 'München',
            country: 'DE',
            population: 1471508,
            lat: 48.13,
            lon: 11.57,
            distanceKm: 0.612,
          },
          {
            name: 'Pasing',
            country: 'DE',
            population: 40000,
            lat: 48.14,
            lon: 11.46,
            distanceKm: 7.918,
          },
        ],
      },
      engineMicros: 5,
      computeCount: 1,
      httpMillis: 1,
    });
    await TestBed.inject(GeoQueryStore).queryNearest(48.137, 11.576);
    const el = render().nativeElement as HTMLElement;
    expect(el.textContent).toContain('München');
    expect(el.textContent).toContain('0.612 km');
    expect(el.querySelectorAll('.row').length).toBe(2);
  });

  it('shows inline problem-details errors', async () => {
    const store = TestBed.inject(GeoQueryStore);
    const { GeoApiError } = await import('../core/models');
    api.nearest.mockRejectedValue(
      new GeoApiError({
        title: 'Invalid request',
        status: 400,
        detail: 'count must be an integer in [1, 100]',
      }),
    );
    await store.queryNearest(1, 1);
    const el = render().nativeElement as HTMLElement;
    expect(el.querySelector('.error')!.textContent).toContain('count must be');
  });

  it('dismisses the error via the × button', async () => {
    const store = TestBed.inject(GeoQueryStore);
    const { GeoApiError } = await import('../core/models');
    api.nearest.mockRejectedValue(
      new GeoApiError({
        title: 'Invalid request',
        status: 400,
        detail: 'count must be an integer in [1, 100]',
      }),
    );
    await store.queryNearest(1, 1);
    const fixture = render();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.error')).toBeTruthy();
    (el.querySelector('.dismiss') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(store.error()).toBeNull();
    expect(el.querySelector('.error')).toBeFalsy();
  });

  it('hovering a row sets the store highlight', async () => {
    api.nearest.mockResolvedValue({
      data: {
        count: 1,
        cities: [{ name: 'X', country: 'DE', population: 1, lat: 1, lon: 1, distanceKm: 1 }],
      },
      engineMicros: 1,
      computeCount: 1,
      httpMillis: 1,
    });
    const store = TestBed.inject(GeoQueryStore);
    await store.queryNearest(1, 1);
    const fixture = render();
    const row = fixture.nativeElement.querySelector('.row') as HTMLElement;
    row.dispatchEvent(new MouseEvent('mouseenter'));
    expect(store.highlightedIndex()).toBe(0);
    row.dispatchEvent(new MouseEvent('mouseleave'));
    expect(store.highlightedIndex()).toBeNull();
  });
});
