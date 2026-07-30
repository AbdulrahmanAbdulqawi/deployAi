import { ComponentFixture, TestBed } from '@angular/core/testing';
import { StatusBadgeComponent } from './status-badge.component';

describe('StatusBadgeComponent', () => {
  let fixture: ComponentFixture<StatusBadgeComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [StatusBadgeComponent]
    }).compileComponents();

    fixture = TestBed.createComponent(StatusBadgeComponent);
    fixture.componentRef.setInput('status', 'success');
    fixture.detectChanges();
  });

  // The badge renders as a bare dot unless a labelOverride is given, so the label lives in the
  // accessible name rather than the text. This spec asserted textContent and had been failing on
  // every CI run since the dot was introduced — a red build nobody could act on, which made the
  // whole pipeline useless as a signal.
  it('exposes the live label for success to assistive technology', () => {
    const badge = fixture.nativeElement.querySelector('.badge');

    expect(badge.getAttribute('title')).toBe('Live');
    expect(badge.getAttribute('aria-label')).toBe('Live');
  });

  it('renders the label as text when one is given to override the dot', () => {
    fixture.componentRef.setInput('labelOverride', 'Live');
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Live');
  });

  it('maps failed status to plain language', () => {
    fixture.componentRef.setInput('status', 'failed');
    fixture.detectChanges();
    expect(fixture.componentInstance.label).toBe('Failed');
  });
});
