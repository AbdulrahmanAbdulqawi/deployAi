import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ProviderStatusCardComponent } from './provider-status-card.component';

describe('ProviderStatusCardComponent', () => {
  let fixture: ComponentFixture<ProviderStatusCardComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProviderStatusCardComponent]
    }).compileComponents();

    fixture = TestBed.createComponent(ProviderStatusCardComponent);
    fixture.componentRef.setInput('roleLabel', 'Your site');
    fixture.componentRef.setInput('providerName', 'vercel');
    fixture.componentRef.setInput('status', 'success');
    fixture.componentRef.setInput('deployUrl', 'https://example.com');
    fixture.detectChanges();
  });

  it('shows live link on success', () => {
    const link = fixture.nativeElement.querySelector('.live-link');
    expect(link).toBeTruthy();
    expect(link.getAttribute('href')).toBe('https://example.com');
  });
});
