import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DeployPlanComponent } from './deploy-plan.component';
import { DeploymentPlan } from '../../core/models/api.models';

describe('DeployPlanComponent', () => {
  let fixture: ComponentFixture<DeployPlanComponent>;

  const plan: DeploymentPlan = {
    parts: [{ role: 'website', providerName: 'vercel' }],
    confidence: 'high',
    plainSummary: 'Looks like a website. We will put it on fast global hosting.'
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DeployPlanComponent]
    }).compileComponents();

    fixture = TestBed.createComponent(DeployPlanComponent);
    fixture.componentRef.setInput('plan', plan);
    fixture.componentRef.setInput('activeParts', plan.parts);
    fixture.detectChanges();
  });

  it('shows the plain-language summary', () => {
    expect(fixture.nativeElement.textContent).toContain(plan.plainSummary);
  });
});
