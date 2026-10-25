"""A dead sampler/event stream or a gap cannot qualify database continuity."""
import unittest
import continuity_lab as lab


class ObservationTests(unittest.TestCase):
    def test_full_coverage_with_live_events_and_separate_application_outage_passes(self) -> None:
        values = [{"at": second, "database_ready": True, "application_ready": second % 2 == 0} for second in range(10)]
        lab.validate_observations(values, start=0, end=9, errors=[], events_alive=True)

    def test_dead_sampler_events_and_uncovered_start_end_or_middle_are_refused(self) -> None:
        full = [{"at": second, "database_ready": True} for second in range(10)]
        for values, errors, alive in [(full, ["timeout"], True), (full, [], False),
                (full[:1], [], True), (full[4:], [], True), (full[:2] + full[7:], [], True)]:
            with self.subTest(values=values, errors=errors, alive=alive), self.assertRaises(RuntimeError):
                lab.validate_observations(values, start=0, end=9, errors=errors, events_alive=alive)
