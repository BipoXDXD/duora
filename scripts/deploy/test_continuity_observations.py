"""A dead sampler/event stream or a gap cannot qualify database continuity."""
import unittest
import continuity_lab as lab


class ObservationTests(unittest.TestCase):
    def test_a_negative_probe_or_an_uncovered_end_can_never_qualify_continuity(self) -> None:
        full = [{"at": second, "database_ready": True} for second in range(10)]
        negative = [dict(value, database_ready=index != 4) for index, value in enumerate(full)]
        for values in (negative, full[:-4], [], full[::-1]):
            with self.subTest(values=values), self.assertRaises(RuntimeError):
                lab.validate_observations(values, start=0, end=9, errors=[], events_alive=True)

    def test_three_second_coverage_limit_and_live_events_are_preserved(self) -> None:
        exact = [{"at": second, "database_ready": True} for second in (0, 3, 6, 9)]
        lab.validate_observations(exact, start=0, end=9, errors=[], events_alive=True)
        late = [{"at": second, "database_ready": True} for second in (0, 3.001, 6, 9)]
        with self.assertRaises(RuntimeError):
            lab.validate_observations(late, start=0, end=9, errors=[], events_alive=True)
        with self.assertRaises(RuntimeError):
            lab.validate_observations(exact, start=0, end=9, errors=[], events_alive=False)

    def test_transport_admission_cannot_extend_the_three_second_initial_coverage_limit(self) -> None:
        for first in (3.001, 5):
            with self.subTest(first=first), self.assertRaises(RuntimeError):
                lab.validate_observations([{'at': first, 'database_ready': True},
                                           {'at': first + 0.25, 'database_ready': True}],
                                          start=0, end=first + 0.5, errors=[], events_alive=True)

    def test_full_coverage_with_live_events_and_separate_application_outage_passes(self) -> None:
        values = [{"at": second, "database_ready": True, "application_ready": second % 2 == 0} for second in range(10)]
        lab.validate_observations(values, start=0, end=9, errors=[], events_alive=True)

    def test_dead_sampler_events_and_uncovered_start_end_or_middle_are_refused(self) -> None:
        full = [{"at": second, "database_ready": True} for second in range(10)]
        for values, errors, alive in [(full, ["timeout"], True), (full, [], False),
                (full[:1], [], True), (full[4:], [], True), (full[:2] + full[7:], [], True)]:
            with self.subTest(values=values, errors=errors, alive=alive), self.assertRaises(RuntimeError):
                lab.validate_observations(values, start=0, end=9, errors=errors, events_alive=alive)
