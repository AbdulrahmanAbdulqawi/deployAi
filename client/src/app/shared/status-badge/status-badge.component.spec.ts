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

  it('renders live label for success', () => {
    expect(fixture.nativeElement.textContent).toContain('Live');
  });

  it('maps failed status to plain language', () => {
    fixture.componentRef.setInput('status', 'failed');
    fixture.detectChanges();
    expect(fixture.componentInstance.label).toBe('Failed');
  });
});
