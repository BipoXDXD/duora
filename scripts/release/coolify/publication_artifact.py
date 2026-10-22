"""Find the newest AMD64 publication within this trusted workflow run, including reruns."""
import os
import re

from gate import api


def select(artifacts):
    candidates = [a for a in artifacts if a.get('expired') is False
                  and re.fullmatch(r'release-amd64-[1-9][0-9]*', a.get('name', ''))]
    if not candidates:
        raise ValueError('No unexpired AMD64 publication in this workflow run')
    return max(candidates, key=lambda a: a['id'])['name']


def main():
    repository = os.environ['GITHUB_REPOSITORY']
    run_id = os.environ['GITHUB_RUN_ID']
    if repository != 'BipoXDXD/duora' or not run_id.isdigit():
        raise ValueError('Only this repository and workflow run qualify')
    response = api(f'repos/{repository}/actions/runs/{run_id}/artifacts?per_page=100')
    if response['total_count'] != len(response['artifacts']):
        raise ValueError('Incomplete artifact inventory')
    name = select(response['artifacts'])
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write(f'name={name}\n')


if __name__ == '__main__':
    main()
